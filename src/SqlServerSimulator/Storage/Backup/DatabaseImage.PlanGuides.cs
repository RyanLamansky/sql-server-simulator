using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage.Backup;

// The plan guides a backup carries, with the id counter they draw from. An
// OBJECT guide names its module by object id, which the restore's replay
// gives back to the module.
partial class DatabaseImage
{
    public readonly List<PlanGuide> PlanGuides = [];
    public int LastPlanGuideId = 65535;

    private void CapturePlanGuides(Database database)
    {
        this.LastPlanGuideId = database.LastPlanGuideId;
        this.PlanGuides.AddRange(database.PlanGuides.ToArray().Select(static entry => entry.Value).OrderBy(static guide => guide.Id));
    }

    /// <summary>Replaces <paramref name="database"/>'s plan guides with the image's.</summary>
    private void ApplyPlanGuides(Database database)
    {
        database.LastPlanGuideId = this.LastPlanGuideId;
        database.PlanGuides.Clear();
        foreach (var guide in this.PlanGuides)
        {
            database.PlanGuides[guide.Name] = new PlanGuide(guide.Id, guide.Name, guide.Scope, guide.QueryText, guide.ScopeObjectId, guide.ScopeBatch, guide.Parameters, guide.Hints, guide.CreateDate)
            {
                ModifyDate = guide.ModifyDate,
                IsDisabled = guide.IsDisabled,
            };
        }
    }

    private void WritePlanGuides(BinaryWriter w)
    {
        w.Write(this.LastPlanGuideId);
        w.Write(this.PlanGuides.Count);
        foreach (var guide in this.PlanGuides)
        {
            w.Write(guide.Id);
            w.Write(guide.Name);
            w.Write((byte)guide.Scope);
            w.Write(guide.QueryText);
            w.Write(guide.ScopeObjectId ?? 0);
            BackupBinary.WriteNullable(w, guide.ScopeBatch);
            BackupBinary.WriteNullable(w, guide.Parameters);
            BackupBinary.WriteNullable(w, guide.Hints);
            w.Write(guide.CreateDate.Ticks);
            w.Write(guide.ModifyDate.Ticks);
            w.Write(guide.IsDisabled);
        }
    }

    private static void ReadPlanGuides(BinaryReader r, DatabaseImage image)
    {
        image.LastPlanGuideId = r.ReadInt32();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var id = r.ReadInt32();
            var name = r.ReadString();
            var scope = (PlanGuideScope)r.ReadByte();
            var queryText = r.ReadString();
            var objectId = r.ReadInt32();
            var scopeBatch = BackupBinary.ReadNullable(r);
            var parameters = BackupBinary.ReadNullable(r);
            var hints = BackupBinary.ReadNullable(r);
            var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            image.PlanGuides.Add(new PlanGuide(id, name, scope, queryText, scope == PlanGuideScope.Object ? objectId : null, scopeBatch, parameters, hints, created)
            {
                ModifyDate = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
                IsDisabled = r.ReadBoolean(),
            });
        }
    }
}
