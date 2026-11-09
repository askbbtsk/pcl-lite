using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xaml;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Codecs;
using PCL.Core.Utils.Hash;
using PCL.Core.Utils.OS;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Size = System.Windows.Size;

using static PCL.ModBase;

namespace PCL;

internal static class NumericUtilities
{



    /// <summary>
    ///     2~65 进制的转换。
    /// </summary>
    public static string RadixConvert(string input, int fromRadix, int toRadix)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz/+=";
        // 零与负数的处理
        if (string.IsNullOrEmpty(input))
            return "0";
        var isNegative = input.StartsWithF("-");
        if (isNegative)
            input = input.TrimStart('-');
        // 转换为十进制
        var realNum = 0L;
        var scale = 1L;
        foreach (var digit in input.Reverse().Select(l => digits.IndexOfF(l.ToString())))
        {
            realNum += digit * scale;
            scale *= fromRadix;
        }

        // 转换为指定进制
        var result = "";
        while (realNum > 0L)
        {
            var newNum = (int)(realNum % toRadix);
            realNum = (long)Math.Round((realNum - newNum) / (double)toRadix);
            result = digits[newNum] + result;
        }

        // 负数的结束处理与返回
        return (isNegative ? "-" : "") + result;
    }



    /// <summary>
    ///     计算二阶贝塞尔曲线。
    /// </summary>
    public static double MathBezier(double x, double x1, double y1, double x2, double y2, double acc = 0.01d)
    {
        if (x <= 0d || double.IsNaN(x)) return 0d;
        if (x >= 1d) return 1d;
        double a, b;
        a = x;
        do
        {
            b = 3 * a * ((0.33333333 + x1 - x2) * a * a + (x2 - 2 * x1) * a + x1);
            a += (x - b) * 0.5;
        } while (!(Math.Abs(b - x) < acc)); // 精度

        return 3 * a * ((0.33333333 + y1 - y2) * a * a + (y2 - 2 * y1) * a + y1);
    }



    /// <summary>
    ///     将一个数字限制为 0~255 的 Byte 值。
    /// </summary>
    public static byte MathByte(double d)
    {
        if (d < 0d)
            d = 0d;
        if (d > 255d)
            d = 255d;
        return (byte)Math.Round(Math.Round(d));
    }



    /// <summary>
    ///     提供 MyColor 类型支持的 Math.Round。
    /// </summary>
    public static MyColor MathRound(MyColor col, int w = 0)
    {
        return new MyColor
            { a = Math.Round(col.a, w), r = Math.Round(col.r, w), g = Math.Round(col.g, w), b = Math.Round(col.b, w) };
    }



    /// <summary>
    ///     获取两数间的百分比。小数点精确到 6 位。
    /// </summary>
    /// <returns></returns>
    public static double MathPercent(double valueA, double valueB, double percent)
    {
        return Math.Round(valueA * (1d - percent) + valueB * percent, 6); // 解决 Double 计算错误
    }



    /// <summary>
    ///     获取两颜色间的百分比，根据 RGB 计算。小数点精确到 6 位。
    /// </summary>
    public static MyColor MathPercent(MyColor valueA, MyColor valueB, double percent)
    {
        return MathRound(valueA * (1d - percent) + valueB * percent, 6); // 解决Double计算错误
    }



    /// <summary>
    ///     将数值限定在某个范围内。
    /// </summary>
    public static double MathClamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }



    /// <summary>
    ///     符号函数。
    /// </summary>
    public static int MathSgn(double value)
    {
        if (value == 0d) return 0;

        if (value > 0d) return 1;

        return -1;
    }

}
