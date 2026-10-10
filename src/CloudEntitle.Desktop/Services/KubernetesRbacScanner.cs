namespace CloudEntitle.Desktop.Services;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public sealed class KubernetesRbacScanner : IKubernetesRbacProvider
{
    private sealed record RoleDefinition(string Kind, string Namespace, string Name, IReadOnlyList<CloudPermission> Permissions, bool IsPrivileged);
    private sealed record WorkloadIdentityAnnotation(CloudProviderType Provider, string Identity);

    private readonly ICloudCommandRunner _commandRunner;
    private readonly CloudWorkloadTrustVerifier _trustVerifier;

    public KubernetesRbacScanner(ICloudCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
        _trustVerifier = new CloudWorkloadTrustVerifier(commandRunner);
    }

    public async Task<IReadOnlyList<CloudAccount>> DiscoverClustersAsync(CancellationToken cancellationToken = default)
    {
        var configResult = await RunKubectlAsync(new[] { "config", "view", "--output=json" }, cancellationToken);
        if (!configResult.IsSuccess) return Array.Empty<CloudAccount>();

        using var config = JsonDocument.Parse(configResult.StandardOutput);
        var root = config.RootElement;
        if (!root.TryGetProperty("contexts", out var contexts) || contexts.ValueKind != JsonValueKind.Array)
            return Array.Empty<CloudAccount>();

        return contexts.EnumerateArray()
            .Select(contextEntry => CreateClusterAccount(root, contextEntry))
            .Where(cluster => cluster is not null)
            .Cast<CloudAccount>()
            .ToArray();
    }

    public Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken = default, CloudProviderType? providerFilter = null)
        => ScanKubernetesRbacAsync(cancellationToken, providerFilter, null);

    public async Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken, CloudProviderType? providerFilter, IReadOnlySet<string>? selectedClusterKeys)
    {
        var messages = new List<string>();
        var coverage = new List<ScanCoverageRecord>();
        var configResult = await RunKubectlAsync(new[] { "config", "view", "--output=json" }, cancellationToken);
        if (!configResult.IsSuccess)
        {
            var details = configResult.StandardError.Trim();
            coverage.Add(Coverage("Kubernetes", "kubeconfig contexts", StatusForError(details), 0, details));
            return new KubernetesRbacScanResult(Array.Empty<KubernetesClusterScan>(), new[] { $"kubectl context discovery failed: {details}" }) { Coverage = coverage };
        }

        using var config = JsonDocument.Parse(configResult.StandardOutput);
        var root = config.RootElement;
        var clusters = new List<KubernetesClusterScan>();
        if (!root.TryGetProperty("contexts", out var contexts) || contexts.ValueKind != JsonValueKind.Array)
        {
            coverage.Add(Coverage("Kubernetes", "kubeconfig contexts", ScanCoverageStatus.NoData, 0, "kubectl returned no configured contexts."));
            return new KubernetesRbacScanResult(clusters, new[] { "kubectl returned no configured contexts." }) { Coverage = coverage };
        }

        coverage.Add(Coverage("Kubernetes", "kubeconfig contexts", contexts.GetArrayLength() == 0 ? ScanCoverageStatus.NoData : ScanCoverageStatus.Collected, contexts.GetArrayLength(), "Contexts visible to the current kubectl configuration."));

        foreach (var contextEntry in contexts.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contextName = GetString(contextEntry, "name");
            if (string.IsNullOrWhiteSpace(contextName) || !contextEntry.TryGetProperty("context", out var context)) continue;
            var clusterName = GetString(context, "cluster") ?? contextName;
            var userName = GetString(context, "user");
            var server = FindNamedEntry(root, "clusters", clusterName, "cluster", "server");
            var execCommand = string.IsNullOrWhiteSpace(userName) ? null : FindNamedEntry(root, "users", userName, "user", "exec", "command");
            var cloudProvider = DetectCloudProvider(contextName, server, execCommand);
            if (providerFilter.HasValue && cloudProvider != providerFilter.Value) continue;
            var cluster = CreateClusterAccount(root, contextEntry)!;
            var selectionKey = LiveScanAccountRecord.CreateSelectionKey(
                cluster.CloudProvider.ToString(), cluster.ExternalAccountId ?? string.Empty, cluster.Description ?? string.Empty);
            if (selectedClusterKeys is not null && !selectedClusterKeys.Contains(selectionKey)) continue;

            try
            {
                var namespacedResult = await RunContextCommandAsync(contextName, new[] { "get", "roles,rolebindings", "--all-namespaces", "--output=json" }, cancellationToken);
                var clusterResult = await RunContextCommandAsync(contextName, new[] { "get", "clusterroles,clusterrolebindings", "--output=json" }, cancellationToken);
                var serviceAccountResult = await RunContextCommandAsync(contextName, new[] { "get", "serviceaccounts", "--all-namespaces", "--output=json" }, cancellationToken);
                var workloadResult = await RunContextCommandAsync(contextName, new[] { "get", "pods,deployments", "--all-namespaces", "--output=json" }, cancellationToken);
                var parsedRoles = new List<RoleDefinition>();
                var bindings = new List<JsonElement>();
                var clusterCoverage = new List<ScanCoverageRecord>();
                if (namespacedResult.IsSuccess)
                {
                    using var document = JsonDocument.Parse(namespacedResult.StandardOutput);
                    parsedRoles.AddRange(ParseRoles(cluster.Id, document.RootElement, "Role"));
                    bindings.AddRange(ParseItems(document.RootElement, "RoleBinding"));
                    clusterCoverage.Add(Coverage(cloudProvider.ToString(), $"{contextName}: namespace RBAC", CoverageStatus(document.RootElement), ParseItems(document.RootElement, "Role").Count + ParseItems(document.RootElement, "RoleBinding").Count, "Roles and RoleBindings visible to the current kubectl identity."));
                }
                else
                {
                    var details = namespacedResult.StandardError.Trim();
                    messages.Add($"Kubernetes/{contextName}: namespace RBAC read failed: {details}");
                    clusterCoverage.Add(Coverage(cloudProvider.ToString(), $"{contextName}: namespace RBAC", StatusForError(details), 0, details));
                }

                if (clusterResult.IsSuccess)
                {
                    using var document = JsonDocument.Parse(clusterResult.StandardOutput);
                    parsedRoles.AddRange(ParseRoles(cluster.Id, document.RootElement, "ClusterRole"));
                    bindings.AddRange(ParseItems(document.RootElement, "ClusterRoleBinding"));
                }
                else
                {
                    var details = clusterResult.StandardError.Trim();
                    messages.Add($"Kubernetes/{contextName}: cluster RBAC read failed: {details}");
                }
                clusterCoverage.Add(clusterResult.IsSuccess
                    ? Coverage(cloudProvider.ToString(), $"{contextName}: cluster RBAC", CoverageStatus(clusterResult), CountItems(clusterResult.StandardOutput, "ClusterRole") + CountItems(clusterResult.StandardOutput, "ClusterRoleBinding"), "ClusterRoles and ClusterRoleBindings visible to the current kubectl identity.")
                    : Coverage(cloudProvider.ToString(), $"{contextName}: cluster RBAC", StatusForError(clusterResult.StandardError), 0, clusterResult.StandardError.Trim()));

                var workloadIdentities = serviceAccountResult.IsSuccess
                    ? ParseWorkloadIdentities(serviceAccountResult.StandardOutput)
                    : new Dictionary<string, WorkloadIdentityAnnotation>(StringComparer.OrdinalIgnoreCase);
                if (!serviceAccountResult.IsSuccess) messages.Add($"Kubernetes/{contextName}: service-account read failed: {serviceAccountResult.StandardError.Trim()}");
                clusterCoverage.Add(serviceAccountResult.IsSuccess
                    ? Coverage(cloudProvider.ToString(), $"{contextName}: ServiceAccounts", CoverageStatus(serviceAccountResult), CountItems(serviceAccountResult.StandardOutput, "ServiceAccount"), "ServiceAccounts and identity annotations visible to the current kubectl identity.")
                    : Coverage(cloudProvider.ToString(), $"{contextName}: ServiceAccounts", StatusForError(serviceAccountResult.StandardError), 0, serviceAccountResult.StandardError.Trim()));
                if (!workloadResult.IsSuccess) messages.Add($"Kubernetes/{contextName}: Pod/Deployment read failed: {workloadResult.StandardError.Trim()}");
                clusterCoverage.Add(workloadResult.IsSuccess
                    ? Coverage(cloudProvider.ToString(), $"{contextName}: Pods and Deployments", CoverageStatus(workloadResult), CountItems(workloadResult.StandardOutput, "Pod") + CountItems(workloadResult.StandardOutput, "Deployment"), "Pods and Deployments visible to the current kubectl identity.")
                    : Coverage(cloudProvider.ToString(), $"{contextName}: Pods and Deployments", StatusForError(workloadResult.StandardError), 0, workloadResult.StandardError.Trim()));
                cluster.ConnectionStatus = namespacedResult.IsSuccess || clusterResult.IsSuccess ? ConnectionStatus.Connected : ConnectionStatus.Failed;

                var roleLookup = parsedRoles.ToDictionary(role => RoleKey(role.Kind, role.Namespace, role.Name), StringComparer.OrdinalIgnoreCase);
                var principalRecords = new Dictionary<string, CloudPrincipal>(StringComparer.OrdinalIgnoreCase);
                var assignments = new List<CloudEffectiveAccessAssignment>();
                var permissions = parsedRoles.SelectMany(role => role.Permissions).ToArray();
                if (serviceAccountResult.IsSuccess)
                {
                    using var serviceAccounts = JsonDocument.Parse(serviceAccountResult.StandardOutput);
                    foreach (var serviceAccount in ParseItems(serviceAccounts.RootElement, "ServiceAccount"))
                    {
                        if (!serviceAccount.TryGetProperty("metadata", out var metadata)) continue;
                        var name = GetString(metadata, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var ns = GetString(metadata, "namespace") ?? "default";
                        var principalId = GetSubjectId("ServiceAccount", name, ns);
                        var annotations = metadata.TryGetProperty("annotations", out var annotationElement) && annotationElement.ValueKind == JsonValueKind.Object
                            ? annotationElement
                            : default;
                        JsonElement ownerAnnotation = default;
                        var hasOwnerAnnotation = annotations.ValueKind == JsonValueKind.Object
                            && annotations.TryGetProperty("yask-ciem.io/owner", out ownerAnnotation);
                        var ownerValue = hasOwnerAnnotation && ownerAnnotation.ValueKind == JsonValueKind.String ? ownerAnnotation.GetString() : null;
                        var ownerIds = string.IsNullOrWhiteSpace(ownerValue) || ownerValue.Equals("unowned", StringComparison.OrdinalIgnoreCase)
                            ? new List<string>()
                            : ownerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        principalRecords[principalId] = new CloudPrincipal
                        {
                            Id = Guid.NewGuid(),
                            AccountId = cluster.Id,
                            CloudProvider = cloudProvider,
                            Name = $"{ns}/{name}",
                            PrincipalId = principalId,
                            IdentityType = IdentityType.ServiceAccount,
                            IsActive = true,
                            OwnerIds = ownerIds,
                            OwnerInventoryComplete = hasOwnerAnnotation,
                            OwnerEvidenceSource = hasOwnerAnnotation ? "Kubernetes yask-ciem.io/owner annotation" : null,
                            FederationInventoryComplete = true,
                            ActivityEvidenceComplete = false,
                            ActivityEvidenceSource = "Current Kubernetes Pod inventory; historical token use is not available."
                        };
                    }
                }
                foreach (var binding in bindings)
                {
                    if (!binding.TryGetProperty("metadata", out var metadata)) continue;
                    var bindingKind = GetString(binding, "kind") ?? "RoleBinding";
                    var bindingNamespace = GetString(metadata, "namespace") ?? "default";
                    if (!binding.TryGetProperty("roleRef", out var roleRef)) continue;
                    var roleKind = GetString(roleRef, "kind") ?? "Role";
                    var roleName = GetString(roleRef, "name") ?? "unknown";
                    var roleKey = RoleKey(roleKind, roleKind == "Role" ? bindingNamespace : string.Empty, roleName);
                    roleLookup.TryGetValue(roleKey, out var roleDefinition);
                    var scope = bindingKind == "ClusterRoleBinding" ? $"clusters/{clusterName}" : $"namespaces/{bindingNamespace}";
                    if (!binding.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array) continue;
                    foreach (var subject in subjects.EnumerateArray())
                    {
                        var kind = GetString(subject, "kind") ?? "User";
                        var name = GetString(subject, "name") ?? "unknown";
                        var subjectNamespace = GetString(subject, "namespace") ?? bindingNamespace;
                        var principalId = GetSubjectId(kind, name, subjectNamespace);
                        var privileged = roleDefinition?.IsPrivileged ?? roleName.Equals("cluster-admin", StringComparison.OrdinalIgnoreCase);
                        principalRecords.TryAdd(principalId, new CloudPrincipal
                        {
                            Id = Guid.NewGuid(), AccountId = cluster.Id, CloudProvider = cloudProvider,
                            Name = principalId, PrincipalId = principalId,
                            IdentityType = kind switch
                            {
                                "Group" => IdentityType.Group,
                                "ServiceAccount" => IdentityType.ServiceAccount,
                                _ => IdentityType.User
                            },
                            IsPrivileged = privileged,
                            IsActive = true
                        });
                        var accessRole = $"{roleKind}/{roleName}";
                        var source = $"Kubernetes {bindingKind} {GetString(metadata, "namespace") ?? "cluster"}/{GetString(metadata, "name") ?? "unknown"}";
                        assignments.Add(new CloudEffectiveAccessAssignment(principalId, accessRole, scope, source, privileged, "Kubernetes RBAC"));
                        if (kind == "ServiceAccount" && workloadIdentities.TryGetValue($"{subjectNamespace}/{name}", out var cloudIdentity) && cloudIdentity.Provider == cloudProvider)
                        {
                            assignments.Add(new CloudEffectiveAccessAssignment(cloudIdentity.Identity, accessRole, scope, $"Kubernetes workload identity via {principalId}", privileged, "ServiceIdentity"));
                        }
                    }
                }

                var roles = parsedRoles.Select(role => new CloudRole
                {
                    Id = Guid.NewGuid(), AccountId = cluster.Id, Name = $"{role.Kind}/{role.Name}", RoleId = RoleKey(role.Kind, role.Namespace, role.Name),
                    Scope = string.IsNullOrWhiteSpace(role.Namespace) ? $"clusters/{clusterName}" : $"namespaces/{role.Namespace}", IsPrivileged = role.IsPrivileged
                }).ToArray();
                var resource = new CloudResource
                {
                    Id = Guid.NewGuid(), AccountId = cluster.Id, ExternalId = clusterName, Name = contextName,
                    Type = "Kubernetes Cluster", ResourceType = ResourceType.Resource, Scope = $"clusters/{clusterName}"
                };
                var traces = workloadResult.IsSuccess
                    ? await BuildWorkloadTracesAsync(workloadResult.StandardOutput, contextName, clusterName, cloudProvider, server, workloadIdentities, assignments, cancellationToken)
                    : Array.Empty<KubernetesWorkloadTrace>();
                foreach (var trace in traces)
                {
                    var serviceAccountId = GetSubjectId("ServiceAccount", trace.ServiceAccount, trace.Namespace);
                    if (!principalRecords.TryGetValue(serviceAccountId, out var principal)) continue;
                    principal.LastObservedWorkloadAtUtc = trace.IsRunningWorkload ? DateTime.UtcNow : principal.LastObservedWorkloadAtUtc;
                    if (trace.Trust.Status == WorkloadTrustStatus.Verified)
                    {
                        principal.FederationTrustVerified = true;
                        if (!string.IsNullOrWhiteSpace(trace.Trust.Issuer) && !string.IsNullOrWhiteSpace(trace.Trust.Subject))
                        {
                            var trust = new WorkloadFederationTrust(trace.Trust.Issuer, trace.Trust.Subject,
                                string.IsNullOrWhiteSpace(trace.Trust.Audience) ? Array.Empty<string>() : new[] { trace.Trust.Audience },
                                trace.Trust.Source ?? "Kubernetes workload trust verifier");
                            if (!principal.FederationTrusts.Any(existing => existing.Issuer == trust.Issuer
                                && existing.Subject == trust.Subject
                                && existing.Source == trust.Source
                                && existing.Audiences.SequenceEqual(trust.Audiences, StringComparer.Ordinal)))
                                principal.FederationTrusts.Add(trust);
                            principal.FederationIssuer = trust.Issuer;
                            principal.FederationSubject = trust.Subject;
                            principal.FederationAudience = string.Join(",", trust.Audiences);
                        }
                    }
                    else if (trace.Trust.Status == WorkloadTrustStatus.Mismatch)
                    {
                        principal.FederationTrustVerified = false;
                    }
                }
                var configuredTraces = traces.Where(trace => !string.IsNullOrWhiteSpace(trace.CloudIdentity)).ToArray();
                var trustStatus = configuredTraces.Length == 0 ? ScanCoverageStatus.NoData
                    : configuredTraces.All(trace => trace.Trust.Status == WorkloadTrustStatus.Verified) ? ScanCoverageStatus.Collected
                    : configuredTraces.Any(trace => trace.Trust.Status == WorkloadTrustStatus.Verified) ? ScanCoverageStatus.Partial
                    : configuredTraces.Any(trace => trace.Trust.Status == WorkloadTrustStatus.Denied) ? ScanCoverageStatus.Denied
                    : configuredTraces.Any(trace => trace.Trust.Status == WorkloadTrustStatus.Unavailable) ? ScanCoverageStatus.Unavailable
                    : ScanCoverageStatus.Partial;
                clusterCoverage.Add(Coverage(cloudProvider.ToString(), $"{contextName}: cloud workload trust", trustStatus, configuredTraces.Length, configuredTraces.Length == 0 ? "No cloud identity annotations were found on referenced ServiceAccounts." : $"{configuredTraces.Count(trace => trace.Trust.Status == WorkloadTrustStatus.Verified)} of {configuredTraces.Length} annotated workload identities verified."));
                coverage.AddRange(clusterCoverage);
                clusters.Add(new KubernetesClusterScan(cluster, principalRecords.Values.ToArray(), roles, permissions, assignments, new[] { resource }) { WorkloadTraces = traces, Coverage = clusterCoverage });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                messages.Add($"Kubernetes/{contextName}: RBAC scan failed: {exception.Message}");
            }
        }

        return new KubernetesRbacScanResult(clusters, messages) { Coverage = coverage };
    }

    private static CloudAccount? CreateClusterAccount(JsonElement root, JsonElement contextEntry)
    {
        var contextName = GetString(contextEntry, "name");
        if (string.IsNullOrWhiteSpace(contextName) || !contextEntry.TryGetProperty("context", out var context)) return null;
        var clusterName = GetString(context, "cluster") ?? contextName;
        var userName = GetString(context, "user");
        var server = FindNamedEntry(root, "clusters", clusterName, "cluster", "server");
        var execCommand = string.IsNullOrWhiteSpace(userName) ? null : FindNamedEntry(root, "users", userName, "user", "exec", "command");
        return new CloudAccount
        {
            Id = Guid.NewGuid(),
            Name = contextName,
            CloudProvider = DetectCloudProvider(contextName, server, execCommand),
            ExternalAccountId = clusterName,
            AccountType = AccountType.Cluster,
            ConnectionStatus = ConnectionStatus.Connected,
            IsReadOnly = true,
            Description = $"Kubernetes context {contextName}; API server {server ?? "unknown"}"
        };
    }

    private async Task<IReadOnlyList<KubernetesWorkloadTrace>> BuildWorkloadTracesAsync(
        string output,
        string contextName,
        string clusterName,
        CloudProviderType provider,
        string? apiServer,
        IReadOnlyDictionary<string, WorkloadIdentityAnnotation> identities,
        IReadOnlyList<CloudEffectiveAccessAssignment> assignments,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(output);
        var traces = new List<KubernetesWorkloadTrace>();
        foreach (var workload in document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var kind = GetString(workload, "kind");
            if (kind is not ("Pod" or "Deployment") || !workload.TryGetProperty("metadata", out var metadata)) continue;
            var ns = GetString(metadata, "namespace") ?? "default";
            var workloadName = GetString(metadata, "name") ?? "unknown";
            var spec = kind == "Pod"
                ? ReadNestedElement(workload, "spec")
                : ReadNestedElement(workload, "spec", "template", "spec");
            var serviceAccount = GetString(spec, "serviceAccountName") ?? "default";
            var identity = identities.GetValueOrDefault($"{ns}/{serviceAccount}");
            var roles = assignments.Where(assignment => assignment.Principal.Equals($"serviceaccount:{ns}:{serviceAccount}", StringComparison.OrdinalIgnoreCase)
                    && assignment.AssignmentType == "Kubernetes RBAC")
                .Select(assignment => assignment.Role).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var tokenInjection = GetNestedString(kind == "Pod" ? workload : ReadNestedElement(workload, "spec", "template"), "metadata", "labels", "azure.workload.identity/use")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            var trust = identity is null
                ? new WorkloadIdentityVerification(WorkloadTrustStatus.NotConfigured, "No supported cloud identity annotation is configured on the ServiceAccount.")
                : identity.Provider != provider
                    ? new WorkloadIdentityVerification(WorkloadTrustStatus.Mismatch, $"The ServiceAccount annotation targets {identity.Provider}, but this context was identified as {provider}.")
                    : await _trustVerifier.VerifyAsync(provider, contextName, apiServer, ns, serviceAccount, identity.Identity, cancellationToken);
            var running = kind == "Pod" && GetNestedString(workload, "status", "phase")?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
            traces.Add(new KubernetesWorkloadTrace(provider, clusterName, ns, kind, workloadName, serviceAccount, identity?.Identity, roles, tokenInjection, trust)
            {
                IsRunningWorkload = running
            });
        }
        return traces;
    }

    private async Task<CloudCommandResult> RunContextCommandAsync(string contextName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await RunKubectlAsync(new[] { "--context", contextName }.Concat(arguments).ToArray(), cancellationToken);

    private Task<CloudCommandResult> RunKubectlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => _commandRunner.RunAsync("kubectl", arguments, cancellationToken);

    private static IReadOnlyList<RoleDefinition> ParseRoles(Guid accountId, JsonElement root, string kind)
    {
        var output = new List<RoleDefinition>();
        foreach (var item in ParseItems(root, kind))
        {
            if (!item.TryGetProperty("metadata", out var metadata)) continue;
            var name = GetString(metadata, "name") ?? "unknown";
            var ns = kind == "Role" ? GetString(metadata, "namespace") ?? "default" : string.Empty;
            var permissions = new List<CloudPermission>();
            var privileged = name.Equals("cluster-admin", StringComparison.OrdinalIgnoreCase);
            if (item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.EnumerateArray())
                {
                    var verbs = GetStringArray(rule, "verbs");
                    var resources = GetStringArray(rule, "resources");
                    var apiGroups = GetStringArray(rule, "apiGroups");
                    if (apiGroups.Count == 0) apiGroups = new[] { string.Empty };
                    privileged |= verbs.Contains("*", StringComparer.Ordinal) || resources.Contains("*", StringComparer.Ordinal);
                    foreach (var verb in verbs)
                    foreach (var resource in resources)
                    foreach (var apiGroup in apiGroups)
                    {
                        var group = string.IsNullOrEmpty(apiGroup) ? "core" : apiGroup;
                        permissions.Add(new CloudPermission
                        {
                            Id = Guid.NewGuid(), AccountId = accountId, Name = $"{verb} {group}/{resource}", Action = verb,
                            Resource = $"{group}/{resource}", Scope = string.IsNullOrWhiteSpace(ns) ? "cluster" : $"namespaces/{ns}",
                            Source = $"Kubernetes {kind} {ns}/{name}", Provider = "Kubernetes", Service = group
                        });
                    }
                    foreach (var url in GetStringArray(rule, "nonResourceURLs"))
                    foreach (var verb in verbs)
                    {
                        permissions.Add(new CloudPermission
                        {
                            Id = Guid.NewGuid(), AccountId = accountId, Name = $"{verb} {url}", Action = verb,
                            Resource = url, Scope = string.IsNullOrWhiteSpace(ns) ? "cluster" : $"namespaces/{ns}",
                            Source = $"Kubernetes {kind} {ns}/{name}", Provider = "Kubernetes", Service = "non-resource URL"
                        });
                    }
                }
            }
            output.Add(new RoleDefinition(kind, ns, name, permissions, privileged));
        }
        return output;
    }

    private static IReadOnlyList<JsonElement> ParseItems(JsonElement root, string kind)
        => root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => GetString(item, "kind") == kind).Select(item => item.Clone()).ToArray()
            : Array.Empty<JsonElement>();

    private static Dictionary<string, WorkloadIdentityAnnotation> ParseWorkloadIdentities(string output)
    {
        using var document = JsonDocument.Parse(output);
        var identities = new Dictionary<string, WorkloadIdentityAnnotation>(StringComparer.OrdinalIgnoreCase);
        foreach (var serviceAccount in ParseItems(document.RootElement, "ServiceAccount"))
        {
            if (!serviceAccount.TryGetProperty("metadata", out var metadata)) continue;
            var ns = GetString(metadata, "namespace") ?? "default";
            var name = GetString(metadata, "name");
            if (string.IsNullOrWhiteSpace(name) || !metadata.TryGetProperty("annotations", out var annotations)) continue;
            foreach (var (key, provider) in new[]
                     {
                         ("eks.amazonaws.com/role-arn", CloudProviderType.Aws),
                         ("iam.gke.io/gcp-service-account", CloudProviderType.Gcp),
                         ("azure.workload.identity/client-id", CloudProviderType.Azure)
                     })
            {
                if (annotations.TryGetProperty(key, out var identity) && !string.IsNullOrWhiteSpace(identity.GetString()))
                {
                    identities[$"{ns}/{name}"] = new WorkloadIdentityAnnotation(provider, identity.GetString()!);
                    break;
                }
            }
        }
        return identities;
    }

    private static ScanCoverageRecord Coverage(string provider, string dataSource, ScanCoverageStatus status, int records, string details)
        => new(provider, dataSource, status, records, details);

    private static ScanCoverageStatus CoverageStatus(JsonElement root)
        => CountItems(root, "Role") + CountItems(root, "RoleBinding") + CountItems(root, "ClusterRole") + CountItems(root, "ClusterRoleBinding") == 0
            ? ScanCoverageStatus.NoData
            : ScanCoverageStatus.Collected;

    private static ScanCoverageStatus CoverageStatus(CloudCommandResult result)
    {
        if (!result.IsSuccess) return StatusForError(result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? ScanCoverageStatus.Collected
            : ScanCoverageStatus.NoData;
    }

    private static int CountItems(string output, string kind)
    {
        using var document = JsonDocument.Parse(output);
        return CountItems(document.RootElement, kind);
    }

    private static int CountItems(JsonElement root, string kind)
    {
        return root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Count(item => GetString(item, "kind") == kind)
            : 0;
    }

    private static ScanCoverageStatus StatusForError(string message)
        => message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            ? ScanCoverageStatus.Denied
            : ScanCoverageStatus.Unavailable;

    private static JsonElement ReadNestedElement(JsonElement element, params string[] path)
    {
        foreach (var property in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out element)) return default;
        }
        return element;
    }

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var value = ReadNestedElement(element, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static CloudProviderType DetectCloudProvider(string context, string? server, string? execCommand)
    {
        var evidence = $"{context} {server} {execCommand}".ToLowerInvariant();
        if (evidence.Contains("eks") || evidence.Contains("aws")) return CloudProviderType.Aws;
        if (evidence.Contains("azmk8s") || evidence.Contains("kubelogin") || evidence.Contains("azure")) return CloudProviderType.Azure;
        if (evidence.Contains("gke") || evidence.Contains("gke-gcloud-auth-plugin") || evidence.Contains("google")) return CloudProviderType.Gcp;
        return CloudProviderType.Kubernetes;
    }

    private static string RoleKey(string kind, string ns, string name) => $"{kind}|{ns}|{name}";
    private static string GetSubjectId(string kind, string name, string ns) => kind switch
    {
        "ServiceAccount" => $"serviceaccount:{ns}:{name}",
        "Group" => $"group:{name}",
        _ => $"user:{name}"
    };

    private static string? FindNamedEntry(JsonElement root, string arrayName, string name, params string[] propertyPath)
    {
        if (!root.TryGetProperty(arrayName, out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        var entry = entries.EnumerateArray().FirstOrDefault(item => GetString(item, "name") == name);
        foreach (var property in propertyPath)
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(property, out entry)) return null;
        }
        return entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
            : Array.Empty<string>();
}
