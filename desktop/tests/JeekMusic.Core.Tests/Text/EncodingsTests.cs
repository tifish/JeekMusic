using JeekMusic.Core.Text;

namespace JeekMusic.Core.Tests.Text;

/// <summary>
/// Core 自己负责注册 GBK、Big5 等代码页：测试工程里没有任何代码调用 Encoding.RegisterProvider，
/// 字节直接写成十六进制，不借助 Encoding.GetEncoding 构造。以前 Encodings 在静态构造里注册，
/// 但静态属性的初始化器先执行，GetEncoding(936) 抛 NotSupportedException（类型初始化失败）。
/// </summary>
public class EncodingsTests
{
    private static readonly byte[] ZhouJieLunGbk = Convert.FromHexString("D6DCBDDCC2D7"); // "周杰伦"
    private static readonly byte[] ZhangXueYouBig5 = Convert.FromHexString("B169BEC7A4CD"); // "張學友"

    [Fact]
    public void DecodesGbkAndBig5WithoutManualRegistration()
    {
        Assert.Equal("周杰伦", Encodings.Gbk.GetString(ZhouJieLunGbk));
        Assert.Equal("張學友", Encodings.Big5.GetString(ZhangXueYouBig5));
        Assert.Equal(932, Encodings.ShiftJis.CodePage);
        Assert.Equal("周杰伦", CharsetDetector.Decode(ZhouJieLunGbk));
        Assert.Equal("張學友", CharsetDetector.Decode(ZhangXueYouBig5));
        // 标签库按 Latin-1 读出的乱码
        Assert.Equal("周杰伦", CharsetFixer.Fix(System.Text.Encoding.Latin1.GetString(ZhouJieLunGbk)));
        Assert.Same(Encodings.Big5, Encodings.ByName("big5"));
    }
}
