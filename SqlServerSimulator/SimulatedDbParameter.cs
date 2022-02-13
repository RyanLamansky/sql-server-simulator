using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

sealed class SimulatedDbParameter : DbParameter
{
    private DbType? dbType;

    public override DbType DbType
    {
        get
        {
            var dbType = this.dbType;
            if (dbType is not null)
                return dbType.Value;

            return this.Value switch
            {
                int => DbType.Int32,
                null => DbType.String,
                _ => throw new ArgumentException($"No mapping exists from object type {this.Value.GetType().FullName} to a known managed provider native type."),
            };
        }
        set => this.dbType = value;
    }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; }

    public override int Size { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    [AllowNull]
    public override string SourceColumn { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public override bool SourceColumnNullMapping { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public override object? Value { get; set; }

    public override void ResetDbType() => this.dbType = null;
}
