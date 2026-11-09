using CloudEntitle.Domain;
using CloudEntitle.RiskEngine.Models;

namespace CloudEntitle.Analytics.Services;

public sealed record PermissionActionMapping(string? PermissionAction,
    PermissionActionMappingStatus Status, string Evidence);

public sealed class ProviderActionPermissionMapper
{
    private static readonly IReadOnlyDictionary<(string Source, string Operation), string> AwsCandidates =
        new Dictionary<(string, string), string>(new AwsMappingKeyComparer())
        {
            [("s3.amazonaws.com", "GetObject")] = "s3:GetObject",
            [("s3.amazonaws.com", "PutObject")] = "s3:PutObject",
            [("s3.amazonaws.com", "DeleteObject")] = "s3:DeleteObject",
            [("s3.amazonaws.com", "ListObjects")] = "s3:ListBucket",
            [("s3.amazonaws.com", "ListObjectsV2")] = "s3:ListBucket",
            [("iam.amazonaws.com", "CreateUser")] = "iam:CreateUser",
            [("iam.amazonaws.com", "CreateAccessKey")] = "iam:CreateAccessKey",
            [("iam.amazonaws.com", "AttachUserPolicy")] = "iam:AttachUserPolicy",
            [("iam.amazonaws.com", "PutUserPolicy")] = "iam:PutUserPolicy",
            [("iam.amazonaws.com", "PassRole")] = "iam:PassRole",
            [("ec2.amazonaws.com", "DescribeInstances")] = "ec2:DescribeInstances",
            [("ec2.amazonaws.com", "StartInstances")] = "ec2:StartInstances",
            [("ec2.amazonaws.com", "StopInstances")] = "ec2:StopInstances"
        };

    public PermissionActionMapping Map(CloudProvider provider, string action, string? permissionAction,
        string? permissionEvidenceSource)
    {
        if (string.IsNullOrWhiteSpace(action))
            return new(null, PermissionActionMappingStatus.Unknown, "The provider event has no action.");
        if (!string.IsNullOrWhiteSpace(permissionAction))
            return new(permissionAction, PermissionActionMappingStatus.ExactProviderEvidence,
                string.IsNullOrWhiteSpace(permissionEvidenceSource)
                    ? $"{provider} event carries an explicit permission action."
                    : $"{provider} {permissionEvidenceSource} supplies the explicit permission action.");

        if (provider == CloudProvider.Aws)
        {
            var separator = action.LastIndexOf('/');
            if (separator > 0 && separator < action.Length - 1
                && AwsCandidates.TryGetValue((action[..separator], action[(separator + 1)..]), out var candidate))
                return new(candidate, PermissionActionMappingStatus.CuratedCandidate,
                    "Curated AWS CloudTrail API-to-IAM candidate mapping v1; API operation is not proof this IAM permission authorized the request.");
        }

        return new(null, PermissionActionMappingStatus.Unknown,
            $"{provider} API operation is not treated as an IAM permission without provider authorization evidence or a reviewed exact mapping.");
    }

    private sealed class AwsMappingKeyComparer : IEqualityComparer<(string Source, string Operation)>
    {
        public bool Equals((string Source, string Operation) x, (string Source, string Operation) y)
            => string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Operation, y.Operation, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Source, string Operation) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Operation));
    }
}
