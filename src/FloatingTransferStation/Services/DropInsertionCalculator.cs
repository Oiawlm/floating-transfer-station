namespace FloatingTransferStation.Services;

public static class DropInsertionCalculator
{
    public static int ForTarget(int targetIndex, double pointerY, double targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        return targetHeight > 0 && pointerY >= targetHeight / 2
            ? checked(targetIndex + 1)
            : targetIndex;
    }

    public static int ForEmptySpace(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        return itemCount;
    }
}
