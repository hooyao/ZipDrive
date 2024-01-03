namespace zip2vd.core;

public interface IVdService
{
    public void Mount();

    public void Unmount();

    public void CompactCache();
}