namespace Detective.Core.Analysis;

internal static class Matrix
{
    public static int[][] Empty(int size)
    {
        var m = new int[size][];
        for (var i = 0; i < size; i++) m[i] = new int[size];
        return m;
    }
}
