namespace Observability.Domain.Identity;

/// <summary>
/// Issue 8.6 RBAC roles. Read scope: Admin/Developer/Viewer can read every app; AppOwner is
/// restricted to the apps assigned via <see cref="UserApplicationAssignment"/>. Admin is the only
/// role permitted on the admin/provisioning surface.
/// </summary>
public enum Role
{
    Viewer = 1,
    Developer = 2,
    AppOwner = 3,
    Admin = 4,
}
