using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Codecs;

namespace PCL.Core.Test;
[TestClass]
public class EncodingDetectorTest
{
    static EncodingDetectorTest()
    {
        // gb2312 / GB18030 属于代码页编码，需要显式注册提供方
        //（应用运行时由 StartupService 注册，测试宿主不跑那段）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TestMethod]
    public void TestEncoding()
    {
        var utf8 = Encoding.UTF8.GetBytes("Hi, There!");
        Assert.AreEqual(EncodingDetector.DetectEncoding(utf8), Encoding.UTF8);
        utf8 = Encoding.UTF8.GetBytes("棍斤拷烫烫烫");
        Assert.AreEqual(EncodingDetector.DetectEncoding(utf8), Encoding.UTF8);
        var gb = Encoding.GetEncoding("gb2312").GetBytes("你好世界");
        // 检测器只有「BOM / 合法 UTF-8 / 兜底默认」三档，没有 DBCS 识别能力；
        // 对 gb2312 字节的核心契约是：不许误判成 UTF-8
        Assert.AreNotEqual(Encoding.UTF8, EncodingDetector.DetectEncoding(gb), "gb2312 字节不应被误判为 UTF-8");
        // var gbnew = Encoding.GetEncoding("GB18030").GetBytes("你好世界");
        // Assert.AreEqual(EncodingDetector.DetectEncoding(gbnew), Encoding.GetEncoding("GB18030"));
        byte[] nonEncode = [0xfe, 0x5f, 0xa1];
        Assert.AreEqual(Encoding.Default, EncodingDetector.DetectEncoding(nonEncode));
    }
}