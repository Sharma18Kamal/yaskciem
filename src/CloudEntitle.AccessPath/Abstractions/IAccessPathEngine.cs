using CloudEntitle.AccessPath.Models;
using DomainAccessPath = CloudEntitle.Domain.Entities.AccessPath;

namespace CloudEntitle.AccessPath.Abstractions;

public interface IAccessPathEngine
{
    Task<AccessPathResult> BuildAsync(AccessPathRequest request, CancellationToken cancellationToken = default);
}

public interface IAccessPathFormatter
{
    string Format(DomainAccessPath path);
}
