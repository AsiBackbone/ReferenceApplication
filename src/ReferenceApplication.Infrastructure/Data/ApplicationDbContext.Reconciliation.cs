using Microsoft.EntityFrameworkCore;
using ReferenceApplication.Infrastructure.Data.Entities;

namespace ReferenceApplication.Infrastructure.Data;

public sealed partial class ApplicationDbContext
{
    public DbSet<ApplicationAuditReconciliationFinding> ApplicationAuditReconciliationFindings =>
        Set<ApplicationAuditReconciliationFinding>();

    public DbSet<ApplicationAuditReconciliationRemediation> ApplicationAuditReconciliationRemediations =>
        Set<ApplicationAuditReconciliationRemediation>();
}
