# Kubernetes RBAC Read-Only Connector

The desktop scan reads contexts already present in the local kubeconfig through `kubectl`. It does not create contexts, refresh credentials, or modify clusters. The current scanner recognizes AKS, EKS, and GKE from context names, API-server hostnames, or configured exec-auth commands; other contexts are labeled Kubernetes.

## Discovery

- Lists kubeconfig contexts without requesting raw credentials.
- Reads namespaced Roles and RoleBindings, cluster-scoped ClusterRoles and ClusterRoleBindings, and ServiceAccounts using read-only `kubectl get` calls.
- Maps role rules into permissions and resolves each binding's subjects and role reference into effective-access rows with namespace or cluster scope.
- Marks wildcard verbs/resources and the `cluster-admin` role as privileged.
- Maps supported ServiceAccount annotations to workload identities: `eks.amazonaws.com/role-arn`, `iam.gke.io/gcp-service-account`, and `azure.workload.identity/client-id`.
- Includes discovered cluster rows and RBAC results in the live scan and scan history.

## Coverage Boundaries

Discovery only covers contexts in the current kubeconfig that the active `kubectl` credentials can read. It does not enumerate every AKS/EKS/GKE cluster in a cloud account, inspect Kubernetes audit logs, or prove that an annotated cloud identity can assume/use its cloud role. Workload-identity rows show the configured ServiceAccount-to-identity annotation path alongside Kubernetes RBAC, not a verified cloud-side trust or permission evaluation.

The scanner reads RBAC objects and ServiceAccounts only. It does not enumerate pods/deployments, infer which workloads use each ServiceAccount, or mutate any Kubernetes resource.
