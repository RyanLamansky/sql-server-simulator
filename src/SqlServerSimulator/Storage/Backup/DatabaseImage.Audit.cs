using SqlServerSimulator.Audit;

namespace SqlServerSimulator.Storage.Backup;

// The database audit specifications a backup carries, with the id counter they
// draw from. A restore brings each back as it was — its id, dates, state and
// the audit GUID it names, an audit the restoring server may not hold — and an
// enabled one names records for that audit at once (probed 2026-10-17 against
// SQL Server 2025).
partial class DatabaseImage
{
    public readonly List<AuditSpecification> AuditSpecifications = [];
    public int LastAuditSpecificationId = 65535;

    private void CaptureAuditSpecifications(Database database)
    {
        this.LastAuditSpecificationId = database.LastAuditSpecificationId;
        this.AuditSpecifications.AddRange(database.AuditSpecifications.ToArray().Select(static entry => entry.Value.Clone()).OrderBy(static specification => specification.Id));
    }

    /// <summary>Replaces <paramref name="database"/>'s audit specifications with the image's.</summary>
    private void ApplyAuditSpecifications(Database database)
    {
        database.LastAuditSpecificationId = this.LastAuditSpecificationId;
        database.AuditSpecifications.Clear();
        foreach (var specification in this.AuditSpecifications)
            database.AuditSpecifications[specification.Name] = specification.Clone();
    }

    private void WriteAuditSpecifications(BinaryWriter w)
    {
        w.Write(this.LastAuditSpecificationId);
        w.Write(this.AuditSpecifications.Count);
        foreach (var specification in this.AuditSpecifications)
        {
            w.Write(specification.Id);
            w.Write(specification.Name);
            w.Write(specification.CreateDate.Ticks);
            w.Write(specification.ModifyDate.Ticks);
            w.Write(specification.AuditGuid.ToByteArray());
            w.Write(specification.Enabled);
            w.Write(specification.Details.Count);
            foreach (var detail in specification.Details)
            {
                w.Write(detail.Action.Name ?? "");
                w.Write(detail.SecurableClass);
                w.Write(detail.MajorId);
                w.Write(detail.PrincipalId);
            }
        }
    }

    private static void ReadAuditSpecifications(BinaryReader r, DatabaseImage image)
    {
        image.LastAuditSpecificationId = r.ReadInt32();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var modified = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var specification = new AuditSpecification(id, name, created, new Guid(r.ReadBytes(16))) { ModifyDate = modified, Enabled = r.ReadBoolean() };
            for (var d = r.ReadInt32(); d > 0; d--)
            {
                var action = AuditCatalog.Instance.DatabaseGroupOrAction(r.ReadString())
                    ?? throw new InvalidDataException("A backup names an audit action the catalog lacks.");
                specification.Details.Add(new AuditSpecificationDetail(action, r.ReadByte(), r.ReadInt32(), r.ReadInt32()));
            }
            image.AuditSpecifications.Add(specification);
        }
    }
}
