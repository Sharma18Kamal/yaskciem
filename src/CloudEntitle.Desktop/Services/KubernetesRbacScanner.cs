namespace CloudEntitle.Desktop.Services;

using System.Text.Json;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;

public sealed class KubernetesRbacScanner : IKubernetesRbacProvider
{
    private sealed record RoleDefinition(string Kind, string Namespace, string Name, IReadOnlyList<CloudPermission> Permissions, bool IsPrivileged);

    private readonly ICloudCommandRunner _commandRunner;

    public KubernetesRbacScanner(ICloudCommandRunner commandRunner) => _commandRunner = commandRunner;

    public async Task<KubernetesRbacScanResult> ScanKubernetesRbacAsync(CancellationToken cancellationToken = default, CloudProviderType? providerFilter = null)
    {
        var messages = new List<string>();
        var configResult = await RunKubectlAsync(new[] { "config", "view", "--output=json" }, cancellationToken);
        if (!configResult.IsSuccess)
        {
            return new KubernetesRbacScanResult(Array.Empty<KubernetesClusterScan>(), new[] { $"kubectl context discovery failed: {configResult.StandardError.Trim()}" });
        }

        using var config = JsonDocument.Parse(configResult.StandardOutput);
        var root = config.RootElement;
        var clusters = new List<KubernetesClusterScan>();
        if (!root.TryGetProperty("contexts", out var contexts) || contexts.ValueKind != JsonValueKind.Array)
        {
            return new KubernetesRbacScanResult(clusters, new[] { "kubectl returned no configured contexts." });
        }

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
            var cluster = new CloudAccount
            {
                Id = Guid.NewGuid(),
                Name = contextName,
                CloudProvider = cloudProvider,
                ExternalAccountId = clusterName,
                AccountType = AccountType.Cluster,
                ConnectionStatus = ConnectionStatus.Connected,
                IsReadOnly = true,
                Description = $"Kubernetes context {contextName}; API server {server ?? "unknown"}"
            };

            try
            {
                var namespacedResult = await RunContextCommandAsync(contextName, new[] { "get", "roles,rolebindings", "--all-namespaces", "--output=json" }, cancellationToken);
                var clusterResult = await RunContextCommandAsync(contextName, new[] { "get", "clusterroles,clusterrolebindings", "--output=json" }, cancellationToken);
                var serviceAccountResult = await RunContextCommandAsync(contextName, new[] { "get", "serviceaccounts", "--all-namespaces", "--output=json" }, cancellationToken);
                var parsedRoles = new List<RoleDefinition>();
                var bindings = new List<JsonElement>();
                if (namespacedResult.IsSuccess)
                {
                    using var document = JsonDocument.Parse(namespacedResult.StandardOutput);
                    parsedRoles.AddRange(ParseRoles(cluster.Id, document.RootElement, "Role"));
                    bindings.AddRange(ParseItems(document.RootElement, "RoleBinding"));
                }
                else messages.Add($"Kubernetes/{contextName}: namespace RBAC read failed: {namespacedResult.StandardError.Trim()}");

                if (clusterResult.IsSuccess)
                {
                    using var document = JsonDocument.Parse(clusterResult.StandardOutput);
                    parsedRoles.AddRange(ParseRoles(cluster.Id, document.RootElement, "ClusterRole"));
                    bindings.AddRange(ParseItems(document.RootElement, "ClusterRoleBinding"));
                }
                else messages.Add($"Kubernetes/{contextName}: cluster RBAC read failed: {clusterResult.StandardError.Trim()}");

                var workloadIdentities = serviceAccountResult.IsSuccess
                    ? ParseWorkloadIdentities(serviceAccountResult.StandardOutput)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!serviceAccountResult.IsSuccess) messages.Add($"Kubernetes/{contextName}: service-account read failed: {serviceAccountResult.StandardError.Trim()}");
                cluster.ConnectionStatus = namespacedResult.IsSuccess || clusterResult.IsSuccess ? ConnectionStatus.Connected : ConnectionStatus.Failed;

                var roleLookup = parsedRoles.ToDictionary(role => RoleKey(role.Kind, role.Namespace, role.Name), StringComparer.OrdinalIgnoreCase);
                var principalRecords = new Dictionary<string, CloudPrincipal>(StringComparer.OrdinalIgnoreCase);
                var assignments = new List<CloudEffectiveAccessAssignment>();
                var permissions = parsedRoles.SelectMany(role => role.Permissions).ToArray();
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
                        if (kind == "ServiceAccount" && workloadIdentities.TryGetValue($"{subjectNamespace}/{name}", out var cloudIdentity))
                        {
                            assignments.Add(new CloudEffectiveAccessAssignment(cloudIdentity, accessRole, scope, $"Kubernetes workload identity via {principalId}", privileged, "ServiceIdentity"));
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
                clusters.Add(new KubernetesClusterScan(cluster, principalRecords.Values.ToArray(), roles, permissions, assignments, new[] { resource }));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                messages.Add($"Kubernetes/{contextName}: RBAC scan failed: {exception.Message}");
            }
        }

        return new KubernetesRbacScanResult(clusters, messages);
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

    private static Dictionary<string, string> ParseWorkloadIdentities(string output)
    {
        using var document = JsonDocument.Parse(output);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    identities[$"{ns}/{name}"] = identity.GetString()!;
                    break;
                }
            }
        }
        return identities;
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
