using System.Collections.Concurrent;
using System.Globalization;

namespace SqlServerSimulator.Audit;

/// <summary>
/// One server's SQL Server Audit state: its audits, its server audit
/// specifications and the <c>.sqlaudit</c> files its audits have written.
/// Made on first use by <see cref="Simulation.AuditsFor"/>; a database's own
/// specifications live on the <see cref="Database"/>, which a backup carries.
/// </summary>
internal sealed class AuditServer(Simulation simulation)
{
    /// <summary>Guards every audit and specification and their changes.</summary>
    public readonly Lock Gate = new();

    private readonly Simulation simulation = simulation;

    private readonly Dictionary<string, ServerAudit> audits = new(BuiltInToken.Comparer);

    private readonly Dictionary<string, AuditSpecification> serverSpecifications = new(BuiltInToken.Comparer);

    /// <summary>
    /// The last <c>audit_id</c> handed out: real numbers audits from 65536
    /// server-wide, never reuses one, and spends one on every <c>CREATE
    /// SERVER AUDIT</c> past its permission check, one a refused option or
    /// a taken name stops included (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    private int lastAuditId = 65535;

    /// <summary>How many audits have started, which numbers each run's stand-in address.</summary>
    private int starts;

    /// <summary>The files the audits have written, by full path; a file outlives its audit, as one on disk does.</summary>
    public readonly ConcurrentDictionary<string, AuditFile> Files = new(StringComparer.Ordinal);

    public int AllocateAuditId()
    {
        lock (this.Gate)
            return ++this.lastAuditId;
    }

    /// <summary>Every audit, in <c>audit_id</c> order.</summary>
    public ServerAudit[] Audits()
    {
        lock (this.Gate)
            return [.. this.audits.Values.OrderBy(static audit => audit.Id)];
    }

    public ServerAudit? Find(string name)
    {
        lock (this.Gate)
            return this.audits.GetValueOrDefault(name);
    }

    public ServerAudit? FindByGuid(Guid guid)
    {
        lock (this.Gate)
        {
            foreach (var (_, audit) in this.audits)
            {
                if (audit.Guid == guid)
                    return audit;
            }
            return null;
        }
    }

    /// <summary>Adds an audit; false when its name or GUID is taken.</summary>
    public bool TryAdd(ServerAudit audit)
    {
        lock (this.Gate)
        {
            foreach (var (_, existing) in this.audits)
            {
                if (existing.Guid == audit.Guid)
                    return false;
            }
            return this.audits.TryAdd(audit.Name, audit);
        }
    }

    /// <summary>Renames an audit; false when the new name is taken.</summary>
    public bool TryRename(ServerAudit audit, string name)
    {
        lock (this.Gate)
        {
            if (!BuiltInToken.Comparer.Equals(audit.Name, name) && this.audits.ContainsKey(name))
                return false;
            _ = this.audits.Remove(audit.Name);
            audit.Name = name;
            this.audits[name] = audit;
            return true;
        }
    }

    public void Remove(ServerAudit audit)
    {
        lock (this.Gate)
            _ = this.audits.Remove(audit.Name);
    }

    /// <summary>Every server audit specification, in id order.</summary>
    public AuditSpecification[] ServerSpecifications()
    {
        lock (this.Gate)
            return [.. this.serverSpecifications.Values.OrderBy(static specification => specification.Id)];
    }

    public AuditSpecification? FindServerSpecification(string name)
    {
        lock (this.Gate)
            return this.serverSpecifications.GetValueOrDefault(name);
    }

    /// <summary>The server specification writing to <paramref name="guid"/>'s audit, other than <paramref name="except"/>, or null.</summary>
    public AuditSpecification? ServerSpecificationFor(Guid guid, AuditSpecification? except = null)
    {
        lock (this.Gate)
        {
            foreach (var (_, specification) in this.serverSpecifications)
            {
                if (specification.AuditGuid == guid && !ReferenceEquals(specification, except))
                    return specification;
            }
            return null;
        }
    }

    public bool TryAddServerSpecification(AuditSpecification specification)
    {
        lock (this.Gate)
            return this.serverSpecifications.TryAdd(specification.Name, specification);
    }

    public void RemoveServerSpecification(AuditSpecification specification)
    {
        lock (this.Gate)
            _ = this.serverSpecifications.Remove(specification.Name);
    }

    /// <summary>
    /// Starts <paramref name="audit"/>: a <c>TO FILE</c> audit opens
    /// <c>name_GUID_0_FILETIME.sqlaudit</c> under its path, the FILETIME its
    /// start to the millisecond, and the audit writes its own <c>AUSC</c>
    /// record (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    public void Start(ServerAudit audit, SimulatedDbConnection connection, DateTime utcNow)
    {
        RunningAudit running;
        lock (this.Gate)
        {
            if (audit.Running is not null)
                return;
            AuditFile? file = null;
            if (audit.Destination == AuditDestination.File)
            {
                var ticks = utcNow.Ticks - (utcNow.Ticks % TimeSpan.TicksPerMillisecond);
                var fileTime = new DateTime(ticks, DateTimeKind.Utc).ToFileTimeUtc();
                var path = audit.FilePath + audit.Name + "_" + audit.Guid.ToString("D").ToUpperInvariant() + "_0_" + fileTime.ToString(CultureInfo.InvariantCulture) + ".sqlaudit";
                file = this.Files.GetOrAdd(path, static path => new AuditFile(path));
            }
            var address = ExtendedEvents.RunningEventSession.StandInAddress(0x400 + ++this.starts);
            running = audit.Running = new RunningAudit(audit, utcNow, address, file);
            this.Publish();
        }
        AuditEvents.SessionChanged(connection, running, "<action>event enabled</action><startup_type>manual</startup_type><object><![CDATA[audit_event]]></object>");
    }

    /// <summary>Stops <paramref name="audit"/>, whose last records say so: <c>destroyed</c>, then <c>event disabled</c>.</summary>
    public void Stop(ServerAudit audit, SimulatedDbConnection connection)
    {
        if (audit.Running is not { } running)
            return;
        AuditEvents.SessionChanged(connection, running, "<action>destroyed</action>");
        AuditEvents.SessionChanged(connection, running, "<action>event disabled</action><object><![CDATA[audit_event]]></object>");
        lock (this.Gate)
        {
            audit.Running = null;
            this.Publish();
        }
    }

    /// <summary>Publishes the running audits to <see cref="Simulation.RunningAudits"/>, null while none runs.</summary>
    private void Publish()
    {
        var running = new List<RunningAudit>();
        foreach (var audit in this.audits.Values.OrderBy(static audit => audit.Id))
        {
            if (audit.Running is { } run)
                running.Add(run);
        }
        this.simulation.RunningAudits = running.Count == 0 ? null : [.. running];
    }

    /// <summary>Publishes again after a change that adds or removes a running audit from the list, such as a rollback.</summary>
    public void Republish()
    {
        lock (this.Gate)
            this.Publish();
    }
}
