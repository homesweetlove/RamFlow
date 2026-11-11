namespace ResearchLab;

// Synthetic CPU-only fixture. One completed unit is one million dependent
// xorshift64 steps. This is not an app workload or a performance improvement.
static class CpuWork
{
    public const string WorkloadId="xorshift64-1000000-v1";
    public static string Complete(int seed) {
        ulong x=(ulong)seed^0x9e3779b97f4a7c15UL;
        for(int i=0;i<1_000_000;i++) {x^=x<<13;x^=x>>7;x^=x<<17;}
        return x.ToString("x16",System.Globalization.CultureInfo.InvariantCulture);
    }
}
