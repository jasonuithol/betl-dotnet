using Betl.Core;

namespace Betl.Components;

public interface IDataComponent
{
    string Id { get; }
    Schema OutputSchema { get; }
    IEnumerable<Row> Stream();
}

public interface ISink
{
    string Id { get; }
    void Drain(IDataComponent input);
}
