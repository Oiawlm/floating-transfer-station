namespace FloatingTransferStation.Services;

public readonly record struct StoredImage(Guid Id, string RelativePath, string AbsolutePath);
