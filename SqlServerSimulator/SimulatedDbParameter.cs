using System;
using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

sealed class SimulatedDbParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string? ParameterName { get; set; }
    public override int Size { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override string SourceColumn { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override bool SourceColumnNullMapping { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override object? Value { get; set; }

    public override void ResetDbType()
    {
        throw new NotImplementedException();
    }
}
