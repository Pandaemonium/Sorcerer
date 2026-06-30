namespace Sorcerer.Magic.Operations;

public sealed record OperationSpec(
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    IReadOnlyList<string> RequiredFields);

