using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudEntitle.Domain;
using CloudEntitle.Integration.Contracts;
using CloudEntitle.Integration.Services;
using CloudEntitle.LicenseServer.Services;
using Xunit;

namespace CloudEntitle.Integration.Tests;

public sealed class AuthoritativeDecisionTests
{
    public static EffectiveDecisionConnection Connection(Guid? tenant = null, Guid? organization = null) => new()
    {
        TenantId = tenant ?? Guid.NewGuid(), OrganizationId = organization ?? Guid.NewGuid(), SourceId = "native-gcp",
        AccountId = "sample-project-123", CollectorIdentity = "collector@example.invalid",
        ExpectedOAuthSubjectId = "123456789012345678901",
        OAuthTokenEnvironmentVariable = "YASK_GCP_DECISION_TOKEN", Enabled = true, AcceptGcpPreview = true,
        Principals = ["alice@example.invalid"], PrivilegedPermissions = ["resourcemanager.projects.setIamPolicy"]
    };
    public static CaptureEffectiveDecisionRequest Request(EffectiveDecisionConnection source)
        => new(source.SourceId, source.Principals[0], source.PrivilegedPermissions[0], source.FullResourceName, true);
    public static string NativeResponse(EffectiveDecisionConnection source, bool granted, string etag)
    {
        var request = Request(source);
        var access = granted ? "ALLOW_ACCESS_STATE_GRANTED" : "ALLOW_ACCESS_STATE_NOT_GRANTED";
        var member = granted ? "user:alice@example.invalid" : "user:bob@example.invalid";
        return JsonSerializer.Serialize(new
        {
            overallAccessState = granted ? "CAN_ACCESS" : "CANNOT_ACCESS",
            accessTuple = new { principal = request.Principal, permission = request.Permission,
                permissionFqdn = "cloudresourcemanager.googleapis.com/projects.setIamPolicy",
                fullResourceName = request.FullResourceName, conditionContext = new { effectiveTags = Array.Empty<object>() } },
            allowPolicyExplanation = new
            {
                allowAccessState = access,
                explainedPolicies = new[] { new { allowAccessState = access, fullResourceName = source.FullResourceName,
                    policy = new { etag, version = 3, bindings = new[] { new { role = "roles/resourcemanager.projectIamAdmin",
                        members = new[] { member } } } },
                    bindingExplanations = new[] { new { allowAccessState = access, role = "roles/resourcemanager.projectIamAdmin",
                        rolePermission = "ROLE_PERMISSION_INCLUDED",
                        combinedMembership = new { membership = granted ? "MEMBERSHIP_MATCHED" : "MEMBERSHIP_NOT_MATCHED" },
                        memberships = new Dictionary<string, object> { [member] =
                            new { membership = granted ? "MEMBERSHIP_MATCHED" : "MEMBERSHIP_NOT_MATCHED" } } } } } }
            },
            denyPolicyExplanation = new
            {
                denyAccessState = "DENY_ACCESS_STATE_NOT_DENIED", permissionDeniable = true,
                explainedResources = new[] { new { fullResourceName = source.FullResourceName,
                    denyAccessState = "DENY_ACCESS_STATE_NOT_DENIED", explainedPolicies = Array.Empty<object>() } }
            },
            pabPolicyExplanation = new { principalAccessBoundaryAccessState = "PAB_ACCESS_STATE_NOT_ENFORCED",
                explainedBindingsAndPolicies = Array.Empty<object>() }
        });
    }

    [Fact]
    public void AuthoritativeDecision_UsesOverallNativeAllowDenyPabStateAndExactTuple()
    {
        var source = Connection();
        foreach (var granted in new[] { false, true })
        {
            var evaluation = GcpEffectiveDecisionEvaluator.Evaluate(NativeResponse(source, granted, "etag-1"),
                Request(source), [source.FullResourceName]);
            Assert.Equal(granted ? "GRANTED" : "NOT_GRANTED", evaluation.Decision);
            Assert.Equal("CompleteVerified", evaluation.Coverage);
            Assert.Empty(evaluation.Blockers);
            Assert.Contains(evaluation.PolicyVersions, value => value.Contains("etag-1"));
        }
        var response = JsonNode.Parse(NativeResponse(source, true, "etag-1"))!;
        response["overallAccessState"] = "CANNOT_ACCESS";
        var inconsistent = GcpEffectiveDecisionEvaluator.Evaluate(response.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal("NOT_GRANTED", inconsistent.Decision);
        Assert.Equal("Partial", inconsistent.Coverage);
        Assert.Contains("provider_policy_decision_inconsistent", inconsistent.Blockers);
    }

    [Theory]
    [InlineData("missing-pab")]
    [InlineData("unknown-pab")]
    [InlineData("unknown-allow")]
    [InlineData("unknown-membership")]
    [InlineData("missing-etag")]
    [InlineData("missing-binding")]
    [InlineData("missing-deny")]
    [InlineData("condition")]
    [InlineData("error")]
    [InlineData("tuple")]
    [InlineData("context")]
    [InlineData("missing-hierarchy")]
    [InlineData("missing-ancestor")]
    [InlineData("future-schema")]
    public void AuthoritativeDecision_FailsClosedOnUnknownPartialConditionalOrUnverifiedEvidence(string fault)
    {
        var source = Connection();
        var root = JsonNode.Parse(NativeResponse(source, true, "etag-1"))!;
        IReadOnlyList<string>? hierarchy = [source.FullResourceName];
        var policy = root["allowPolicyExplanation"]!["explainedPolicies"]![0]!;
        switch (fault)
        {
            case "missing-pab": root.AsObject().Remove("pabPolicyExplanation"); break;
            case "unknown-pab": root["pabPolicyExplanation"]!["principalAccessBoundaryAccessState"] = "PAB_ACCESS_STATE_UNKNOWN_INFO"; break;
            case "unknown-allow": root["allowPolicyExplanation"]!["allowAccessState"] = "ALLOW_ACCESS_STATE_UNKNOWN_INFO"; break;
            case "unknown-membership": policy["bindingExplanations"]![0]!["combinedMembership"]!["membership"] = "MEMBERSHIP_UNKNOWN_INFO"; break;
            case "missing-etag": policy["policy"]!.AsObject().Remove("etag"); break;
            case "missing-binding": policy.AsObject().Remove("bindingExplanations"); break;
            case "missing-deny": root.AsObject().Remove("denyPolicyExplanation"); break;
            case "condition": policy["policy"]!["bindings"]![0]!["condition"] = new JsonObject { ["expression"] = "true" }; break;
            case "error": root["errors"] = new JsonArray(new JsonObject { ["code"] = 7 }); break;
            case "tuple": root["accessTuple"]!["principal"] = "different@example.invalid"; break;
            case "context": root["accessTuple"]!["conditionContext"]!["request"] = new JsonObject { ["receiveTime"] = DateTimeOffset.UtcNow.ToString("O") }; break;
            case "missing-hierarchy": hierarchy = null; break;
            case "missing-ancestor": hierarchy = [source.FullResourceName, "//cloudresourcemanager.googleapis.com/organizations/123"]; break;
            case "future-schema": root["additionalPolicyExplanation"] = new JsonObject { ["accessState"] = "UNKNOWN" }; break;
        }
        var evaluation = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), hierarchy);
        Assert.Equal("Partial", evaluation.Coverage);
        Assert.NotEmpty(evaluation.Blockers);
    }

    [Fact]
    public void AuthoritativeDecision_RejectsDuplicatePropertiesAndNormalizesObjectAndTagOrdering()
    {
        var source = Connection();
        var duplicate = NativeResponse(source, true, "etag").Replace("\"overallAccessState\":", "\"overallAccessState\":\"CAN_ACCESS\",\"overallAccessState\":");
        Assert.Contains("duplicate_provider_json_property",
            GcpEffectiveDecisionEvaluator.Evaluate(duplicate, Request(source), [source.FullResourceName]).Blockers);
        var root = JsonNode.Parse(NativeResponse(source, true, "etag"))!;
        root["accessTuple"]!["conditionContext"]!["effectiveTags"] = new JsonArray(
            new JsonObject { ["tagKey"] = "tagKeys/2", ["tagValue"] = "tagValues/2" },
            new JsonObject { ["tagValue"] = "tagValues/1", ["tagKey"] = "tagKeys/1" });
        var first = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        root["accessTuple"]!["conditionContext"]!["effectiveTags"] = new JsonArray(
            new JsonObject { ["tagKey"] = "tagKeys/1", ["tagValue"] = "tagValues/1" },
            new JsonObject { ["tagValue"] = "tagValues/2", ["tagKey"] = "tagKeys/2" });
        var second = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal(first.ContextSha256, second.ContextSha256);
    }

    [Fact]
    public void AuthoritativeDecision_EvaluatesBoundPabPoliciesAndRequiresTheirNativeVersionAndCompleteExplanation()
    {
        var source = Connection();
        var root = JsonNode.Parse(NativeResponse(source, true, "allow-etag"))!;
        var policyName = "organizations/123/locations/global/principalAccessBoundaryPolicies/boundary";
        root["pabPolicyExplanation"] = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            principalAccessBoundaryAccessState = "PAB_ACCESS_STATE_ALLOWED",
            explainedBindingsAndPolicies = new[]
            {
                new
                {
                    bindingAndPolicyAccessState = "PAB_ACCESS_STATE_ALLOWED",
                    explainedPolicyBinding = new
                    {
                        policyBindingState = "POLICY_BINDING_STATE_ENFORCED",
                        policyBinding = new { name = "organizations/123/locations/global/policyBindings/binding",
                            etag = "binding-etag", policy = policyName, policyKind = "PRINCIPAL_ACCESS_BOUNDARY",
                            target = new { principalSet = source.FullResourceName } }
                    },
                    explainedPolicy = new
                    {
                        policyAccessState = "PAB_ACCESS_STATE_ALLOWED",
                        policy = new { name = policyName, etag = "pab-etag",
                            details = new { enforcementVersion = "1", rules = new[] { new { effect = "ALLOW",
                                resources = new[] { source.FullResourceName } } } } },
                        policyVersion = new { version = 1, enforcementState = "PAB_POLICY_ENFORCEMENT_STATE_ENFORCED" },
                        explainedRules = new[] { new { ruleAccessState = "PAB_ACCESS_STATE_ALLOWED", effect = "ALLOW",
                            combinedResourceInclusionState = "RESOURCE_INCLUSION_STATE_INCLUDED",
                            explainedResources = new[] { new { resource = source.FullResourceName,
                                resourceInclusionState = "RESOURCE_INCLUSION_STATE_INCLUDED" } } } }
                    }
                }
            }
        }));
        var complete = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal("CompleteVerified", complete.Coverage);
        Assert.Contains(complete.PolicyVersions, version => version.Contains("pab-etag"));
        Assert.Contains(complete.PolicyVersions, version => version.StartsWith("pab-enforcementversion|"));
        root["pabPolicyExplanation"]!["explainedBindingsAndPolicies"]![0]!["explainedPolicy"]!.AsObject().Remove("policyVersion");
        var partial = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal("Partial", partial.Coverage);
        Assert.Contains("pab_enforcement_version_missing", partial.Blockers);
    }

    [Fact]
    public void AuthoritativeDecision_UsesDocumentedDenyPolicyAndRuleShapeAndNeverPromotesAllowOnlyGrant()
    {
        var source = Connection();
        var root = JsonNode.Parse(NativeResponse(source, true, "allow-etag"))!;
        const string permission = "cloudresourcemanager.googleapis.com/projects.setIamPolicy";
        const string principal = "principal://goog/subject/alice@example.invalid";
        root["overallAccessState"] = "CANNOT_ACCESS";
        root["denyPolicyExplanation"] = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            denyAccessState = "DENY_ACCESS_STATE_DENIED",
            permissionDeniable = true,
            explainedResources = new[]
            {
                new
                {
                    fullResourceName = source.FullResourceName,
                    denyAccessState = "DENY_ACCESS_STATE_DENIED",
                    explainedPolicies = new[]
                    {
                        new
                        {
                            denyAccessState = "DENY_ACCESS_STATE_DENIED",
                            policy = new
                            {
                                name = "policies/cloudresourcemanager.googleapis.com%2Fprojects%2F123456789/denypolicies/deny",
                                etag = "deny-etag", kind = "DenyPolicy",
                                rules = new[] { new { denyRule = new
                                {
                                    deniedPermissions = new[] { permission }, deniedPrincipals = new[] { principal }
                                } } }
                            },
                            ruleExplanations = new[]
                            {
                                new
                                {
                                    denyAccessState = "DENY_ACCESS_STATE_DENIED",
                                    combinedDeniedPermission = new { permissionMatchingState = "PERMISSION_PATTERN_MATCHED" },
                                    combinedDeniedPrincipal = new { membership = "MEMBERSHIP_MATCHED" },
                                    deniedPermissions = new Dictionary<string, object>
                                    {
                                        [permission] = new { permissionMatchingState = "PERMISSION_PATTERN_MATCHED" }
                                    },
                                    deniedPrincipals = new Dictionary<string, object>
                                    {
                                        [principal] = new { membership = "MEMBERSHIP_MATCHED" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }));
        var complete = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal("NOT_GRANTED", complete.Decision);
        Assert.Equal("CompleteVerified", complete.Coverage);
        Assert.Contains(complete.PolicyVersions, version => version.Contains("deny-etag"));
        root["denyPolicyExplanation"]!["explainedResources"]![0]!["explainedPolicies"]![0]!["ruleExplanations"]![0]!
            .AsObject().Remove("denyAccessState");
        var partial = GcpEffectiveDecisionEvaluator.Evaluate(root.ToJsonString(), Request(source), [source.FullResourceName]);
        Assert.Equal("Partial", partial.Coverage);
        Assert.Contains("deny_rule_state_missing", partial.Blockers);
    }

    public static EffectiveDecisionSnapshot Snapshot(EffectiveDecisionConnection source, bool granted, string version,
        DateTimeOffset completed)
    {
        var raw = NativeResponse(source, granted, version);
        var result = GcpEffectiveDecisionEvaluator.Evaluate(raw, Request(source), [source.FullResourceName]);
        return new(Guid.NewGuid(), CloudProvider.Gcp, source.AccountId, source.SourceId, source.ConfigurationSha256,
            source.CollectorIdentity, source.Principals[0], source.PrivilegedPermissions[0], source.FullResourceName,
            result.ContextSha256, completed, completed.AddSeconds(-1), completed, result.Decision, result.Coverage,
            GcpEffectiveDecisionEvaluator.VerificationProfile, GcpEffectiveDecisionEvaluator.Sha256(raw), result.PolicyVersions, result.Blockers);
    }
    [Fact]
    public void AuthoritativeDecision_OnlyConfirmsVerifiedFreshExactNativeDeniedToGrantedTransition()
    {
        var source = Connection();
        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(source, false, "etag-before", now.AddMinutes(-2));
        var after = Snapshot(source, true, "etag-after", now.AddMinutes(-1));
        var report = new DecisionEscalationAnalysisService().Analyze(source.Describe(), before, after, true, now);
        Assert.Equal("ConfirmedDecisionEscalation", report.Status);
        Assert.Empty(report.Blockers);
        Assert.Contains("not proof", report.Explanation);
        Assert.Equal("NoEscalation", new DecisionEscalationAnalysisService().Analyze(source.Describe(),
            before with { Decision = "GRANTED" }, after, true, now).Status);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("same-version")]
    [InlineData("source")]
    [InlineData("account")]
    [InlineData("principal")]
    [InlineData("action")]
    [InlineData("resource")]
    [InlineData("context")]
    [InlineData("context-time")]
    [InlineData("stale")]
    [InlineData("config")]
    [InlineData("partial")]
    [InlineData("unverified")]
    [InlineData("allowlist")]
    public void AuthoritativeDecision_NeverConfirmsAnUnverifiedOrIncomparableTransition(string fault)
    {
        var source = Connection();
        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(source, false, "etag-before", now.AddMinutes(-2));
        var after = Snapshot(source, true, "etag-after", now.AddMinutes(-1));
        var verified = true;
        switch (fault)
        {
            case "overlap": after = after with { StartedAtUtc = before.CompletedAtUtc }; break;
            case "same-version": after = after with { PolicyVersions = before.PolicyVersions }; break;
            case "source": after = after with { SourceId = "other" }; break;
            case "account": after = after with { AccountId = "another-project" }; break;
            case "principal": after = after with { Principal = "other@example.invalid" }; break;
            case "action": after = after with { Permission = "resourcemanager.projects.get" }; break;
            case "resource": after = after with { FullResourceName = "//cloudresourcemanager.googleapis.com/projects/other-project" }; break;
            case "context": after = after with { ContextSha256 = new string('A', 64) }; break;
            case "context-time": after = after with { ContextObservedAtUtc = after.StartedAtUtc.AddSeconds(-1) }; break;
            case "stale": now = now.AddMinutes(10); break;
            case "config": after = after with { ConfigurationSha256 = new string('B', 64) }; break;
            case "partial": after = after with { Coverage = "Partial" }; break;
            case "unverified": verified = false; break;
            case "allowlist": source = source with { PrivilegedPermissions = [] }; break;
        }
        var report = new DecisionEscalationAnalysisService().Analyze(source.Describe(), before, after, verified, now);
        Assert.NotEqual("ConfirmedDecisionEscalation", report.Status);
        Assert.NotEmpty(report.Blockers);
    }

    [Theory]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Aws)]
    public void AuthoritativeDecision_AzureAndAwsCannotBePromotedBySimulationOrBooleans(CloudProvider provider)
    {
        var source = Connection();
        var now = DateTimeOffset.UtcNow;
        var report = new DecisionEscalationAnalysisService().Analyze(source.Describe() with { Provider = provider },
            Snapshot(source, false, "a", now.AddMinutes(-2)), Snapshot(source, true, "b", now.AddMinutes(-1)), true, now);
        Assert.Equal("Unsupported", report.Status);
    }

    [Fact]
    public async Task AuthoritativeDecision_CollectorAuthenticatesIdentityProjectAndCallsOnlyKnownReadEndpoints()
    {
        var source = Connection();
        var handler = new NativeHandler(source);
        using var client = new HttpClient(handler);
        var collector = new GcpAuthoritativeDecisionCollector(new SingleClientFactory(client), name =>
            name == source.OAuthTokenEnvironmentVariable ? "opaque-test-token" : null);
        var capture = await collector.CaptureAsync(source, Request(source), CancellationToken.None);
        Assert.Equal("CompleteVerified", capture.Snapshot.Coverage);
        Assert.Equal(source.CollectorIdentity, capture.Snapshot.CollectorIdentity);
        Assert.Equal(GcpEffectiveDecisionEvaluator.VerificationProfile, capture.Snapshot.VerificationProfile);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer opaque-test-token", request.Authorization));
        Assert.Equal("https://www.googleapis.com/oauth2/v2/userinfo", handler.Requests[0].Uri);
        Assert.Equal($"https://cloudresourcemanager.googleapis.com/v3/projects/{source.AccountId}", handler.Requests[1].Uri);
        Assert.Equal(GcpEffectiveDecisionEvaluator.Endpoint, handler.Requests[2].Uri);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.DoesNotContain("opaque-test-token", capture.NativeResponseJson);
        var requestJson = JsonNode.Parse(handler.Requests[2].Body!)!;
        Assert.Equal(Request(source).Principal, requestJson["accessTuple"]!["principal"]!.GetValue<string>());
        Assert.False(requestJson.AsObject().ContainsKey("authoritative"));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("subject")]
    [InlineData("project")]
    [InlineData("redirect")]
    [InlineData("oversized")]
    [InlineData("missing-token")]
    [InlineData("consent")]
    [InlineData("outside-sample")]
    [InlineData("preview")]
    public async Task AuthoritativeDecision_CollectorFailsClosedBeforeEvidenceIsSaved(string fault)
    {
        var source = Connection();
        if (fault == "preview") source = source with { AcceptGcpPreview = false };
        var request = Request(source);
        if (fault == "consent") request = request with { ReadConsent = false };
        if (fault == "outside-sample") request = request with { Principal = "outside@example.invalid" };
        using var client = new HttpClient(new NativeHandler(source) { Fault = fault });
        var collector = new GcpAuthoritativeDecisionCollector(new SingleClientFactory(client),
            _ => fault == "missing-token" ? null : "opaque-test-token");
        await Assert.ThrowsAnyAsync<Exception>(() => collector.CaptureAsync(source, request, CancellationToken.None));
    }

    public sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    public sealed class NativeHandler(EffectiveDecisionConnection source) : HttpMessageHandler
    {
        public string? Fault { get; init; }
        public int NativeReads { get; private set; }
        public List<(HttpMethod Method, string Uri, string Authorization, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            if (Fault == "redirect") return new(HttpStatusCode.Redirect) { RequestMessage = request };
            if (Fault == "oversized") return new(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent(new string('x', 1024 * 1024 + 1)) };
            var uri = request.RequestUri.AbsoluteUri;
            var json = uri.Contains("/userinfo", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { email = Fault == "identity" ? "wrong@example.invalid" : source.CollectorIdentity,
                    id = Fault == "subject" ? "99999999999999" : source.ExpectedOAuthSubjectId, verified_email = true })
                : uri.Contains("/projects/", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { projectId = Fault == "project" ? "other-project" : source.AccountId, name = "projects/123456789", state = "ACTIVE" })
                    : NativeResponse(source, ++NativeReads > 1, "etag-" + NativeReads);
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
