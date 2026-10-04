using CloudEntitle.OrganizationManagement.Models;

namespace CloudEntitle.OrganizationManagement.Abstractions;

public interface IOrganizationStore
{
    OrganizationDirectoryState Load();
    void Save(OrganizationDirectoryState state);
}
