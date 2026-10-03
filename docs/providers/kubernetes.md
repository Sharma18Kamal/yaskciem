# Kubernetes RBAC Read-Only Connector

The desktop scan reads contexts already present in the local kubeconfig through `kubectl`. It does not create contexts, refresh credentials, or modify clusters. The current scanner recognizes AKS, EKS, and GKE from context names, API-server hostnames, or configured exec-auth commands; other contexts are labeled Kubernetes.

## Discovery

- Lists kubeconfig contexts without requesting raw credentials.
- Reads namespaced Roles and RoleBindings, cluster-scoped ClusterRoles and ClusterRoleBindings, and ServiceAccounts using read-only `kubectl get` calls.
- Maps role rules into permissions and resolves each binding's subjects and role reference into effective-access rows with namespace or cluster scope.
- Marks wildcard verbs/resources and the `cluster-admin` role as privileged.
- Maps supported ServiceAccount annotations to workload identities: `eks.amazonaws.com/role-arn`, `iam.gke.io/gcp-service-account`, and `azure.workload.identity/client-id`.
- Traces scanned Pods and Deployments through their ServiceAccount and resolved Kubernetes role bindings to the annotated cloud identity. Azure Workload Identity label state is shown where present.
- Verifies EKS OIDC role trust (issuer, subject, audience), GKE `roles/iam.workloadIdentityUser` bindings, and AKS federated credentials (issuer, subject, audience) using the existing provider CLI credentials. The result is `Verified`, `Mismatch`, or an explicit `Denied`/`Unavailable`/`Unsupported` status; it never infers trust from annotations alone.
- Includes discovered cluster rows and RBAC results in the live scan and scan history.

## Coverage Boundaries

Discovery only covers contexts in the current kubeconfig that the active `kubectl` credentials can read. It does not enumerate every AKS/EKS/GKE cluster in a cloud account or inspect Kubernetes audit logs. Cloud trust verification is limited to identities visible through the active Azure/AWS/GCP CLI context and the queried trust objects; it verifies federation trust configuration, not runtime token exchange or final cloud permissions.

The scanner reads RBAC objects, ServiceAccounts, Pods, and Deployments. It does not inspect container images or runtime behavior and does not mutate any Kubernetes resource.
