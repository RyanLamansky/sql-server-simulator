using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The query notification request a TDS request's ALL_HEADERS carried —
/// what <c>SqlDependency</c> and <c>SqlNotificationRequest</c> send: the
/// user's message, the service to notify (<c>service=…;local database=…</c>)
/// and the subscription's timeout in seconds.
/// </summary>
internal sealed class QueryNotificationRequest(string message, string service, string? database, int timeout)
{
    public readonly string Message = message;
    public readonly string Service = service;
    public readonly string? Database = database;
    public readonly int Timeout = timeout;

    /// <summary>
    /// Reads header type 1 of a request's ALL_HEADERS (two length-prefixed
    /// UTF-16 strings and an optional timeout), or null without one.
    /// </summary>
    public static QueryNotificationRequest? FromAllHeaders(byte[] payload)
    {
        var end = Network.TdsSession.SkipAllHeaders(payload);
        var offset = 4;
        while (offset + 6 <= end)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            if (length < 6 || offset + length > end)
                return null;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4)) == 1)
            {
                var at = offset + 6;
                var limit = offset + length;
                string? Text()
                {
                    if (at + 2 > limit)
                        return null;
                    var bytes = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
                    at += 2;
                    if (at + bytes > limit)
                        return null;
                    var text = Encoding.Unicode.GetString(payload, at, bytes);
                    at += bytes;
                    return text;
                }
                if (Text() is not { } message || Text() is not { } deployment)
                    return null;
                var timeout = at + 4 <= limit ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at)) : 432000;
                string? service = null, database = null;
                foreach (var part in deployment.Split(';'))
                {
                    var equals = part.IndexOf('=', StringComparison.Ordinal);
                    if (equals < 0)
                        continue;
                    // SqlDependency quotes the service's name.
                    var key = part[..equals].Trim();
                    var value = part[(equals + 1)..].Trim().Trim('"');
                    if (key.Equals("service", StringComparison.OrdinalIgnoreCase))
                        service = value;
                    else if (key.Equals("local database", StringComparison.OrdinalIgnoreCase))
                        database = value;
                }
                return service is null ? null : new QueryNotificationRequest(message, service, database, timeout <= 0 ? 432000 : timeout);
            }
            offset += length;
        }
        return null;
    }
}

/// <summary>A query notification subscription — a row of <c>sys.dm_qn_subscriptions</c>.</summary>
internal sealed class QuerySubscription(int id, Database database, byte[] sid, HeapTable[] tables, DateTime created, QueryNotificationRequest request)
{
    public readonly int Id = id;
    public readonly Database Database = database;
    public readonly byte[] Sid = sid;
    public readonly HeapTable[] Tables = tables;
    public readonly DateTime Created = created;
    public readonly QueryNotificationRequest Request = request;
}

// Query notifications, the server side of SqlDependency: a query run under a
// notification request subscribes, and the first committed change to a table
// it read sends its QueryNotification message to the requested service from
// the database's QueryNotificationService (probed 2026-10-16 against SQL
// Server 2025); see docs/claude/service-broker.md.
partial class Simulation
{
    /// <summary>The live subscriptions, guarded by itself.</summary>
    internal readonly List<QuerySubscription> QuerySubscriptions = [];

    /// <summary>How many subscriptions are live, read without the lock on every write.</summary>
    internal volatile int QuerySubscriptionCount;

    private int nextQuerySubscriptionId;

    /// <summary>
    /// Subscribes the query <paramref name="text"/> just ran for
    /// <paramref name="request"/>: one real can't watch — a <c>*</c>, a
    /// table not named <c>schema.table</c>, <c>TOP</c>, <c>DISTINCT</c>, a
    /// subquery, an outer join, a set operator, an aggregate without
    /// <c>GROUP BY</c> or <c>COUNT</c> where <c>COUNT_BIG</c> belongs, a
    /// nondeterministic function, a temporary table — is answered at once with
    /// the <c>subscribe</c> / <c>statement</c> / <c>invalid</c> notification;
    /// any other subscribes to the tables it reads.
    /// </summary>
    internal void SubscribeQuery(BatchContext batch, QueryNotificationRequest request, string text)
    {
        var database = batch.CurrentDatabase;
        var sid = LoginSid(batch.Connection.Security.Effective.LoginName);
        if (QueryTablesIfNotifiable(batch, text) is not { Length: > 0 } tables)
        {
            this.SendQueryNotification(database, request, 0, "subscribe", "statement", "invalid", 0, sid);
            return;
        }
        var subscription = new QuerySubscription(Interlocked.Increment(ref this.nextQuerySubscriptionId), database, sid, tables, batch.CurrentStatement.UtcNow, request);
        lock (this.QuerySubscriptions)
        {
            this.QuerySubscriptions.Add(subscription);
            this.QuerySubscriptionCount = this.QuerySubscriptions.Count;
        }
    }

    /// <summary>
    /// The tables a query notification would watch for <paramref name="text"/>,
    /// read off its tokens, or null when real refuses to watch the query.
    /// </summary>
    private static HeapTable[]? QueryTablesIfNotifiable(BatchContext batch, string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (Tokenizer.NextToken(text, ref index, batch.CurrentDatabase.Collation) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }
        var tables = new List<HeapTable>();
        var hasGroupBy = tokens.Exists(static token => token is ReservedKeyword { Keyword: Keyword.Group });
        var selects = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.Select }:
                    if (++selects > 1)
                        return null;
                    break;
                // LEFT and RIGHT before a parenthesis are the string functions.
                case ReservedKeyword { Keyword: Keyword.Left or Keyword.Right } when i + 1 < tokens.Count && tokens[i + 1] is Operator { Character: '(' }:
                    break;
                case ReservedKeyword { Keyword: Keyword.Top or Keyword.Distinct or Keyword.Union or Keyword.Except or Keyword.Intersect or Keyword.Into or Keyword.Outer or Keyword.Left or Keyword.Right or Keyword.Full }:
                    return null;
                case Operator { Character: '*' } when i > 0 && tokens[i - 1] is ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: ',' or '.' }
                    && (i + 1 >= tokens.Count || tokens[i + 1] is ReservedKeyword { Keyword: Keyword.From } or Operator { Character: ',' }):
                    return null;
                case Name { Value: var function } when i + 1 < tokens.Count && tokens[i + 1] is Operator { Character: '(' }:
                    if (BuiltInToken.EqualsAny(function, "COUNT", "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "SYSDATETIMEOFFSET", "NEWID", "RAND", "CURRENT_TIMESTAMP"))
                        return null;
                    if (!hasGroupBy && BuiltInToken.EqualsAny(function, "SUM", "AVG", "MIN", "MAX", "COUNT_BIG", "STDEV", "VAR"))
                        return null;
                    break;
                case ReservedKeyword { Keyword: Keyword.From or Keyword.Join }:
                    {
                        // Each source named after FROM, a comma in the FROM list, or JOIN.
                        var j = i + 1;
                        if (j >= tokens.Count || tokens[j] is not Name)
                            return null;
                        var parts = new List<string>();
                        while (j < tokens.Count && tokens[j] is Name part)
                        {
                            parts.Add(part.Value);
                            if (j + 1 < tokens.Count && tokens[j + 1] is Operator { Character: '.' })
                                j += 2;
                            else
                                break;
                        }
                        if (parts.Count != 2 || parts[1].StartsWith('#') || parts[1].StartsWith('@'))
                            return null;
                        var name = new MultiPartName(parts[0]).WithAddedPart(parts[1], false);
                        if (!batch.TryResolveTable(name, out var table) || table.IsTableVariable)
                            return null;
                        tables.Add(table);
                        break;
                    }
            }
        }
        return [.. tables.Distinct()];
    }

    /// <summary>
    /// A statement wrote <paramref name="table"/>: each subscription reading
    /// it fires once the writing transaction commits, telling what kind of
    /// change — <c>insert</c>, <c>update</c>, <c>delete</c>, <c>truncate</c> —
    /// and is gone.
    /// </summary>
    internal void NoteSubscribedWrite(BatchContext batch, HeapTable table, string info)
    {
        List<QuerySubscription>? hit = null;
        lock (this.QuerySubscriptions)
        {
            foreach (var subscription in this.QuerySubscriptions)
            {
                if (Array.IndexOf(subscription.Tables, table) >= 0)
                    (hit ??= []).Add(subscription);
            }
            if (hit is null)
                return;
            foreach (var subscription in hit)
                _ = this.QuerySubscriptions.Remove(subscription);
            this.QuerySubscriptionCount = this.QuerySubscriptions.Count;
        }
        void Fire()
        {
            foreach (var subscription in hit)
                this.SendQueryNotification(subscription.Database, subscription.Request, subscription.Id, "change", "data", info, subscription.Database.Id, subscription.Sid);
        }
        void Restore()
        {
            lock (this.QuerySubscriptions)
            {
                this.QuerySubscriptions.AddRange(hit);
                this.QuerySubscriptionCount = this.QuerySubscriptions.Count;
            }
        }
        if ((batch.CurrentUndoLog ?? batch.Connection.CurrentTransaction?.UndoLog) is { } log)
            log.RecordBrokerChange(Restore, Fire);
        else
            Fire();
    }

    /// <summary>
    /// Sends one QueryNotification message on a dialog of its own from the
    /// database's QueryNotificationService to the requested service, outside
    /// any transaction; a service that isn't there gets nothing.
    /// </summary>
    private void SendQueryNotification(Database subscriber, QueryNotificationRequest request, int id, string type, string source, string info, int databaseId, byte[] sid)
    {
        var database = request.Database is { } named && this.Databases.TryGetValue(named, out var found) ? found : subscriber;
        var objects = database.ServiceBroker;
        if (!objects.Services.TryGetValue(request.Service, out var target))
            return;
        var from = objects.Services["http://schemas.microsoft.com/SQL/Notifications/QueryNotificationService"];
        var contract = objects.Contracts["http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification"];
        var messageType = objects.MessageTypes["http://schemas.microsoft.com/SQL/Notifications/QueryNotification"];
        var body = new StringBuilder()
            .Append("﻿<qn:QueryNotification xmlns:qn=\"http://schemas.microsoft.com/SQL/Notifications/QueryNotification\" id=\"")
            .Append(id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\" type=\"").Append(type)
            .Append("\" source=\"").Append(source)
            .Append("\" info=\"").Append(info)
            .Append("\" database_id=\"").Append(databaseId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\" sid=\"0x").Append(Convert.ToHexString(sid))
            .Append("\"><qn:Message>").Append(System.Security.SecurityElement.Escape(request.Message))
            .Append("</qn:Message></qn:QueryNotification>")
            .ToString();
        var broker = this.Broker;
        var now = DateTime.UtcNow;
        lock (broker.Gate)
        {
            var conversation = Guid.NewGuid();
            var initiatorGroup = new ConversationGroup(broker.NewId(), database, from);
            var targetGroup = new ConversationGroup(broker.NewId(), database, target);
            broker.Groups[initiatorGroup.Id] = initiatorGroup;
            broker.Groups[targetGroup.Id] = targetGroup;
            var initiator = new ConversationEndpoint(broker.NewId(), conversation, isInitiator: true, database, from, contract, initiatorGroup, target.Name, Database.DboPrincipalId, DateTime.MaxValue.AddDays(-1), encrypted: false)
            {
                State = EndpointState.Conversing,
                SendSequence = 1,
                FarBrokerInstance = BrokerInstance,
            };
            var receiver = new ConversationEndpoint(broker.NewId(), conversation, isInitiator: false, database, target, contract, targetGroup, from.Name, Database.DboPrincipalId, initiator.Lifetime, encrypted: false)
            {
                State = EndpointState.Conversing,
                ReceiveSequence = 1,
                FarPrincipalId = Database.DboPrincipalId,
                FarBrokerInstance = BrokerInstance,
                Far = initiator,
            };
            initiator.Far = receiver;
            broker.Endpoints[initiator.Handle] = initiator;
            broker.Endpoints[receiver.Handle] = receiver;
            if (target.Queue.Settings.Enabled)
                _ = broker.Enqueue(null, receiver, messageType, 0, Encoding.Unicode.GetBytes(body), now);
            Monitor.PulseAll(broker.Gate);
        }
    }
}
