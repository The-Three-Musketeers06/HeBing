namespace AiVisionStudio.Models;

public sealed record ImageInputReference(string OperationId, string OutputKey, string OutputParameter = "");

public sealed record ImageInputSource(ImageInputReference Reference, string OperationName, string OutputName, int Slot, bool Available)
{
    public string Label => OperationName + " / " + OutputName;
}
