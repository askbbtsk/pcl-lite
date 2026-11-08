using System.Threading.Tasks;

namespace PCL.Core.Utils.Diff;

public interface IBinaryDiff
{
    /// <summary>
    /// 生成补丁。产物必须能被 <see cref="ApplyAsync"/> 在给定 originData 上还原出 newData；
    /// 不保证体积最优。
    /// </summary>
    public Task<byte[]> MakeAsync(byte[] originData, byte[] newData);
    public Task<byte[]> ApplyAsync(byte[] originData, byte[] diffData);
}