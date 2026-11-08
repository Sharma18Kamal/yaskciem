using System.Text.Json;
using System.Text;
using CloudEntitle.Application.Interfaces;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class TrustCredentialMonitoringTests
{
    [Fact]
    public async Task AzureCollectorReadsOnlyCredentialMetadataAndFederatedTrustForBoundTenant()
    {
        var subscription = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid().ToString();
        var appObjectId = Guid.NewGuid().ToString();
        var appId = Guid.NewGuid().ToString();
        var credentialId = Guid.NewGuid().ToString();
        var federationId = Guid.NewGuid().ToString();
        var runner = new Runner((command, args) =>
        {
            if (args.Contains("account") && args.Contains("show"))
                return Json(new { id = subscription, tenantId = tenant });
            if (args.Contains("GET") && args.Contains("--url"))
                return Json(new
                {
                    value = new[]
                    {
                        new
                        {
                            id = appObjectId, appId, appOwnerOrganizationId = tenant, displayName = "client workload",
                            passwordCredentials = new[] { new { keyId = credentialId, displayName = "rotation-1",
                                startDateTime = "2026-01-01T00:00:00Z", endDateTime = "2027-01-01T00:00:00Z" } },
                            keyCredentials = Array.Empty<object>()
                        }
                    }
                });
            if (args.Any(argument => argument.Contains("/$batch", StringComparison.Ordinal)))
                return Json(new
                {
                    responses = new[]
                    {
                        new { id = "0", status = 200, body = new { value = new[]
                        {
                            new { id = federationId, name = "oidc trust", issuer = "https://issuer.example.invalid",
                                subject = "system:serviceaccount:ns:workload", audiences = new[] { "api://AzureADTokenExchange" } }
                        } } }
                    }
                });
            throw new InvalidOperationException("Unexpected source command.");
        });

        var snapshot = await new AzureTrustCredentialEvidenceCollector(runner)
            .CollectAsync(new(CloudProvider.Azure, subscription));

        Assert.Equal(CloudProvider.Azure, snapshot.Provider);
        Assert.Equal(subscription, snapshot.AccountId);
        Assert.True(snapshot.CredentialInventoryComplete);
        Assert.True(snapshot.TrustInventoryComplete);
        Assert.Equal(2, snapshot.Observations.Count);
        Assert.Contains(snapshot.Observations, item => item.Kind == "PasswordCredential" && item.EvidenceId == credentialId);
        var trust = Assert.Single(snapshot.Observations, item => item.Kind == "FederatedIdentityCredential");
        Assert.Equal(federationId, trust.EvidenceId);
        Assert.Equal("https://issuer.example.invalid", trust.Issuer);
        Assert.DoesNotContain("secretText", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.All(runner.Calls, call =>
        {
            Assert.Equal("az", call.Command);
            Assert.DoesNotContain("--password", call.Args, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("--private-key", call.Args, StringComparer.OrdinalIgnoreCase);
        });
        SharedWorkspaceStore.ValidateTrustCredentialSnapshot(snapshot);
    }

    [Fact]
    public async Task AzureCollectorRejectsForeignSubscriptionAndMarksFailedFederationReadsIncomplete()
    {
        var expected = Guid.NewGuid().ToString();
        var runner = new Runner((_, args) => args.Contains("show")
            ? Json(new { id = Guid.NewGuid().ToString(), tenantId = Guid.NewGuid().ToString() })
            : throw new InvalidOperationException("Unexpected command."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureTrustCredentialEvidenceCollector(runner).CollectAsync(new(CloudProvider.Azure, expected)));
        Assert.Single(runner.Calls);

        var subscription = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid().ToString();
        var brokenFederation = new Runner((_, args) =>
        {
            if (args.Contains("show")) return Json(new { id = subscription, tenantId = tenant });
            if (args.Contains("GET")) return Json(new { value = new[]
            {
                new { id = Guid.NewGuid().ToString(), appId = Guid.NewGuid().ToString(), appOwnerOrganizationId = tenant,
                    displayName = "app", passwordCredentials = Array.Empty<object>(), keyCredentials = Array.Empty<object>() }
            } });
            if (args.Any(argument => argument.Contains("/$batch", StringComparison.Ordinal)))
                return Json(new { responses = Array.Empty<object>() });
            throw new InvalidOperationException();
        });
        var snapshot = await new AzureTrustCredentialEvidenceCollector(brokenFederation)
            .CollectAsync(new(CloudProvider.Azure, subscription));
        Assert.True(snapshot.CredentialInventoryComplete);
        Assert.False(snapshot.TrustInventoryComplete);
        Assert.Contains(snapshot.Limitations, item => item.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AwsCollectorReadsRoleTrustAndExistingCredentialReportWithoutGeneratingOne()
    {
        const string account = "123456789012";
        const string csv = """
            user,arn,password_enabled,password_last_changed,password_last_used,mfa_active,access_key_1_active,access_key_1_last_rotated,access_key_1_last_used_date,access_key_1_last_used_region,access_key_1_last_used_service,access_key_2_active,access_key_2_last_rotated,access_key_2_last_used_date,access_key_2_last_used_region,access_key_2_last_used_service
            <root_account>,arn:aws:iam::123456789012:root,false,N/A,N/A,true,false,N/A,N/A,N/A,N/A,false,N/A,N/A,N/A,N/A
            workload,arn:aws:iam::123456789012:user/workload,true,2026-01-01T00:00:00Z,N/A,false,true,2026-01-02T00:00:00Z,N/A,N/A,N/A,false,N/A,N/A,N/A,N/A
            """;
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(csv));
        var runner = new Runner((command, args) =>
        {
            Assert.Equal("aws", command);
            if (args.Contains("get-caller-identity")) return Json(new { Account = account });
            if (args.Contains("list-roles")) return Json(new
            {
                Roles = new[]
                {
                    new
                    {
                        RoleName = "workload",
                        RoleId = "AROATEST",
                        AssumeRolePolicyDocument = new
                        {
                            Statement = new[]
                            {
                                new { Effect = "Allow", Principal = new { AWS = "arn:aws:iam::111122223333:root" },
                                    Action = "sts:AssumeRole", Sid = "external", Condition = new { StringEquals = new { account = "tenant" } } }
                            }
                        }
                    }
                },
                NextToken = (string?)null
            });
            if (args.Contains("get-credential-report"))
                return Json(new { State = "COMPLETE", GeneratedTime = "2026-11-08T10:00:00Z", Content = content });
            throw new InvalidOperationException("Unexpected AWS source command.");
        });

        var snapshot = await new MultiCloudTrustCredentialEvidenceCollector(runner)
            .CollectAsync(new(CloudProvider.Aws, account));

        Assert.Equal(CloudProvider.Aws, snapshot.Provider);
        Assert.True(snapshot.TrustInventoryComplete);
        Assert.True(snapshot.CredentialInventoryComplete);
        Assert.Contains(snapshot.Observations, item => item.Kind == "IamRoleTrustPolicy"
            && item.Issuer!.Contains("111122223333", StringComparison.Ordinal));
        Assert.Contains(snapshot.Observations, item => item.Kind == "IamCredentialReport"
            && item.PrincipalName == "workload");
        Assert.Contains(snapshot.Limitations, item => item.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("SecretAccessKey", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Calls, call => call.Args.Contains("generate-credential-report"));
        SharedWorkspaceStore.ValidateTrustCredentialSnapshot(snapshot);
    }

    [Fact]
    public async Task GcpCollectorReadsUserManagedKeysAndProjectAndServiceAccountImpersonationPolicies()
    {
        const string projectId = "sample-project-123";
        const string email = "worker@sample-project-123.iam.gserviceaccount.com";
        var runner = new Runner((command, args) =>
        {
            Assert.Equal("gcloud", command);
            if (args.Contains("describe")) return Json(new { projectId, projectNumber = "123456789012" });
            if (args.Contains("get-iam-policy") && args.Contains("projects"))
                return Json(new
                {
                    bindings = new[] { new { role = "roles/iam.serviceAccountTokenCreator", members = new[] { "user:operator@example.invalid" } } }
                });
            if (args.Contains("keys") && args.Contains("list"))
                return Json(new[] { new { name = $"projects/-/serviceAccounts/{email}/keys/key-id",
                    keyType = "USER_MANAGED", keyOrigin = "GOOGLE_PROVIDED", validAfterTime = "2026-01-01T00:00:00Z",
                    validBeforeTime = "2027-01-01T00:00:00Z", disabled = false } });
            if (args.Contains("list") && args.Contains("service-accounts"))
                return Json(new[] { new { email, uniqueId = "123" } });
            if (args.Contains("get-iam-policy") && args.Contains("service-accounts"))
                return Json(new
                {
                    bindings = new[] { new { role = "roles/iam.serviceAccountUser", members = new[] { "user:admin@example.invalid" } } }
                });
            throw new InvalidOperationException("Unexpected GCP source command.");
        });

        var snapshot = await new MultiCloudTrustCredentialEvidenceCollector(runner)
            .CollectAsync(new(CloudProvider.Gcp, projectId));

        Assert.Equal(CloudProvider.Gcp, snapshot.Provider);
        Assert.True(snapshot.CredentialInventoryComplete);
        Assert.True(snapshot.TrustInventoryComplete);
        Assert.Contains(snapshot.Observations, item => item.Kind == "GcpProjectIamBinding");
        Assert.Contains(snapshot.Observations, item => item.Kind == "GcpServiceAccountIamBinding");
        Assert.Contains(snapshot.Observations, item => item.Kind == "GcpUserManagedServiceAccountKey");
        Assert.Contains(snapshot.Limitations, item => item.Contains("inherited organization/folder", StringComparison.OrdinalIgnoreCase));
        Assert.All(runner.Calls, call =>
        {
            Assert.DoesNotContain("create", call.Args, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("set-iam-policy", call.Args, StringComparer.OrdinalIgnoreCase);
        });
        SharedWorkspaceStore.ValidateTrustCredentialSnapshot(snapshot);
    }

    [Fact]
    public void TrustCredentialDiffSuppressesRemovalWhenEitherInventoryIsIncomplete()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        var observation = Observation("FederatedIdentityCredential", "trust-id", "before");
        var before = Snapshot(Guid.NewGuid(), started, started.AddMinutes(1), [observation], true, true);
        var after = Snapshot(Guid.NewGuid(), started.AddMinutes(1).AddSeconds(1), started.AddMinutes(2), [], true, false);

        var changes = new TrustCredentialMonitoringAnalysisService().Compare(before, after);

        Assert.Empty(changes);
    }

    [Fact]
    public void BaselineSelectionUsesExactPayloadTimeAndExcludesCurrentAndEqualTimeSnapshots()
    {
        var start = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
        var current = Snapshot(Guid.NewGuid(), start, start, [], true, true);
        var prior = Snapshot(Guid.NewGuid(), start.AddMinutes(-1), start.AddTicks(-1), [], true, true);
        var equal = Snapshot(Guid.NewGuid(), start.AddMinutes(-1), start, [], true, true);
        var overlapping = Snapshot(Guid.NewGuid(), start.AddMinutes(-1), start.AddTicks(1), [], true, true);
        var sameIdWithEarlierTime = current with { StartedAtUtc = start.AddMinutes(-1), CollectedAtUtc = start.AddTicks(-1) };
        var candidates = new[] { current, equal, overlapping, sameIdWithEarlierTime, prior };
        var summaries = candidates.Select(item => new TrustCredentialSnapshotSummary(item.SnapshotId,
            item.StartedAtUtc, new DateTimeOffset(item.CollectedAtUtc.Ticks / 10 * 10, TimeSpan.Zero),
            0, true, true, Guid.NewGuid())).ToArray();
        Assert.All(summaries, item => Assert.True(item.CollectedAtUtc < current.StartedAtUtc));

        var analysis = new TrustCredentialMonitoringAnalysisService();
        Assert.Same(prior, analysis.SelectBaseline(current, candidates));
        Assert.Null(analysis.SelectBaseline(current, [current, equal, overlapping, sameIdWithEarlierTime]));
        Assert.Throws<ArgumentException>(() => analysis.Compare(equal, current));
        Assert.Throws<ArgumentException>(() => analysis.Compare(sameIdWithEarlierTime, current));
    }

    [Fact]
    public void TrustCredentialDiffReportsAddedAndChangedMetadataOnlyForComparableCompleteSnapshots()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-3);
        var before = Snapshot(Guid.NewGuid(), started, started.AddSeconds(30),
            [Observation("PasswordCredential", "credential-id", new string('a', 64))], true, true);
        var after = Snapshot(Guid.NewGuid(), started.AddMinutes(1), started.AddMinutes(2),
        [
            Observation("PasswordCredential", "credential-id", new string('b', 64)),
            Observation("FederatedIdentityCredential", "new-trust", new string('c', 64))
        ], true, true);

        var changes = new TrustCredentialMonitoringAnalysisService().Compare(before, after);

        Assert.Contains(changes, item => item.Change == "Changed" && item.Kind == "PasswordCredential");
        Assert.Contains(changes, item => item.Change == "Added" && item.Kind == "FederatedIdentityCredential");
        Assert.All(changes, item => Assert.Contains("not a provider-attested", item.Evidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewlyObservedPrivilegedAssignmentRemainsPotentialWithoutProviderDecisions()
    {
        var prior = DateTimeOffset.UtcNow.AddHours(-2);
        var records = new[]
        {
            new SharedCiemRecordDto(CloudProvider.Azure, "effective-access", "admin", "subscription",
                """{"principal":"app","role":"Owner","scope":"/subscriptions/subscription","source":"Azure RBAC","isPrivileged":true}""")
        };
        var signals = new TrustCredentialMonitoringAnalysisService().CompareObservedAssignments(
            CloudProvider.Azure, "subscription", prior, prior.AddHours(1), true, true, [], records);

        var signal = Assert.Single(signals);
        Assert.Equal("Potential", signal.Confidence);
        Assert.Contains("not authoritative comparable", signal.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Verified", signal.Confidence);
        Assert.Contains("privilege-escalation.potential", OperationalEventTypes.All);
    }

    [Fact]
    public void TrustCredentialSnapshotValidationRejectsInvalidMetadataDigests()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(Guid.NewGuid(), now.AddMinutes(-1), now, [Observation("KeyCredential", "credential", "bad")], true, true);

        Assert.Throws<ArgumentException>(() => SharedWorkspaceStore.ValidateTrustCredentialSnapshot(snapshot));
    }

    private static TrustCredentialSnapshot Snapshot(Guid id, DateTimeOffset start, DateTimeOffset end,
        IReadOnlyList<TrustCredentialObservation> observations, bool credentialsComplete, bool trustComplete)
        => new(id, CloudProvider.Azure, "11111111-1111-1111-1111-111111111111", start, end,
            "Azure CLI / Microsoft Graph Entra application credentials and federation", observations,
            ["Partial source coverage."])
        {
            CredentialInventoryComplete = credentialsComplete,
            TrustInventoryComplete = trustComplete
        };

    private static TrustCredentialObservation Observation(string kind, string evidenceId, string digest)
        => new("app-id", "application", kind, evidenceId, "metadata", "issuer", "subject", ["audience"],
            null, null, digest.Length == 64 ? digest : "not-a-sha256");

    private static CloudCommandResult Json<T>(T value) => new(0, JsonSerializer.Serialize(value), "");

    private sealed class Runner(Func<string, IReadOnlyList<string>, CloudCommandResult> callback) : ICloudCommandRunner
    {
        public List<(string Command, IReadOnlyList<string> Args)> Calls { get; } = [];
        public Task<CloudCommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((command, arguments));
            return Task.FromResult(callback(command, arguments));
        }
    }
}
