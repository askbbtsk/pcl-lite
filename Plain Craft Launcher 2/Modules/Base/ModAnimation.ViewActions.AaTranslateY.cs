using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Network;

using PCL.Core.App.Localization;
namespace PCL;

public static partial class ModAnimation
{

    /// <summary>
    ///     利用 TranslateTransform 移动 Y 轴的动画，这不会造成布局更新。
    /// </summary>
    /// <param name="obj">动画的对象。</param>
    /// <param name="value">进行移动的值。</param>
    /// <param name="time">动画长度（毫秒）。</param>
    /// <param name="delay">动画延迟执行的时间（毫秒）。</param>
    /// <param name="ease">插值器类型。</param>
    /// <param name="after">是否等到以前的动画完成后才继续本动画。</param>
    public static AniData AaTranslateY(object obj, double value, int time = 400, int delay = 0, AniEase ease = null,
        bool after = false)
    {
        return new AniData
        {
            typeMain = AniType.Number,
            typeSub = AniTypeSub.TranslateY,
            timeTotal = time,
            ease = ease ?? new AniEaseLinear(),
            obj = obj,
            value = value,
            isAfter = after,
            timeFinished = -delay
        };
    }


    // 特殊

    /// <summary>
    ///     将一个StackPanel中的各个项目依次显示。
    /// </summary>
    /// <remarks></remarks>
    public static List<AniData> AaStack(StackPanel stack, int time = 100, int delay = 25)
    {
        List<AniData> aaStackRet = default;
        aaStackRet = new List<AniData>();
        var aniDelay = 0;
        foreach (var Item in stack.Children)
        {
            ((dynamic)Item).Opacity = 0;
            aaStackRet.Add(AaOpacity(Item, 1d, time, aniDelay));
            aniDelay += delay;
        }

        return aaStackRet;
    }




    // 基类
    public enum AniEasePower
    {
        Weak = 2,
        Middle = 3,
        Strong = 4,
        ExtraStrong = 5
    }


    /// <summary>
    ///     缓动函数基类。
    /// </summary>
    public abstract class AniEase
    {
        /// <summary>
        ///     获取函数值。
        /// </summary>
        /// <param name="t">时间百分比。</param>
        public abstract double GetValue(double t);

        /// <summary>
        ///     获取增量值。
        /// </summary>
        /// <param name="t1">较大的 X。</param>
        /// <param name="t0">较小的 X。</param>
        public virtual double GetDelta(double t1, double t0)
        {
            return GetValue(t1) - GetValue(t0);
        }
    }


    /// <summary>
    ///     渐入渐出组合。
    /// </summary>
    public class AniEaseInout : AniEase
    {
        private readonly AniEase easeIn;
        private readonly double easeInPercent;
        private readonly AniEase easeOut;

        public AniEaseInout(AniEase easeIn, AniEase easeOut, double easeInPercent = 0.5d)
        {
            this.easeIn = easeIn;
            this.easeOut = easeOut;
            this.easeInPercent = easeInPercent;
        }

        public override double GetValue(double t)
        {
            if (t < easeInPercent) return easeInPercent * easeIn.GetValue(t / easeInPercent);

            return (1d - easeInPercent) * easeOut.GetValue((t - easeInPercent) / (1d - easeInPercent)) + easeInPercent;
        }
    }


    // Linear / 线性
    /// <summary>
    ///     线性，无缓动。
    /// </summary>
    public class AniEaseLinear : AniEase
    {
        public override double GetValue(double t)
        {
            return ModBase.MathClamp(t, 0d, 1d);
        }

        public override double GetDelta(double t1, double t0)
        {
            return ModBase.MathClamp(t1, 0d, 1d) - ModBase.MathClamp(t0, 0d, 1d);
        }
    }


    // Fluent / 平滑
    /// <summary>
    ///     平滑开始。
    /// </summary>
    public class AniEaseInFluent : AniEase
    {
        private readonly AniEasePower p;

        public AniEaseInFluent(AniEasePower power = AniEasePower.Middle)
        {
            p = power;
        }

        public override double GetValue(double t)
        {
            return Math.Pow(ModBase.MathClamp(t, 0d, 1d), (double)p);
        }
    }


    /// <summary>
    ///     平滑结束。
    /// </summary>
    public class AniEaseOutFluent : AniEase
    {
        private readonly AniEasePower p;

        public AniEaseOutFluent(AniEasePower power = AniEasePower.Middle)
        {
            p = power;
        }

        public override double GetValue(double t)
        {
            return 1d - Math.Pow(ModBase.MathClamp(1d - t, 0d, 1d), (double)p);
        }
    }


    /// <summary>
    ///     平滑开始与结束。
    /// </summary>
    public class AniEaseInoutFluent : AniEase
    {
        private readonly AniEaseInout ease;

        public AniEaseInoutFluent(AniEasePower power = AniEasePower.Middle, double middle = 0.5d)
        {
            ease = new AniEaseInout(new AniEaseInFluent(power), new AniEaseOutFluent(power), middle);
        }

        public override double GetValue(double t)
        {
            return ease.GetValue(t);
        }
    }


    /// <summary>
    ///     以特定速度开始的平滑结束。
    /// </summary>
    public class AniEaseOutFluentWithInitial : AniEase
    {
        private readonly double alpha; // (初速度 / 平均速度) – 1

        /// <param name="initialPixelPerSecond">初速度，px/s</param>
        /// <param name="totalSecond">总时长，s</param>
        /// <param name="totalDistance">总路程，px</param>
        public AniEaseOutFluentWithInitial(double initialPixelPerSecond, double totalSecond, double totalDistance)
        {
            var v0_norm = initialPixelPerSecond * totalSecond / totalDistance; // 归一化初速度
            alpha = v0_norm - 1.0d;
            if (alpha < 0d)
                alpha = 0d; // 初速度小于平均速度时，退化为线性
        }

        public override double GetValue(double percent)
        {
            var p = ModBase.MathClamp(percent, 0d, 1d);
            if (alpha == 0d)
                return p; // 退化到线性
            return (alpha + 1d) * p / (1d + alpha * p);
        }
    }


    // Back / 回弹
    /// <summary>
    ///     回弹开始。有效时间为 1/3。
    /// </summary>
    public class AniEaseInBack : AniEase
    {
        private readonly double p;

        public AniEaseInBack(AniEasePower power = AniEasePower.Middle)
        {
            p = 3d - (double)power * 0.5d;
        }

        public override double GetValue(double t)
        {
            t = ModBase.MathClamp(t, 0d, 1d);
            return Math.Pow(t, p) * Math.Cos(1.5d * Math.PI * (1d - t));
        }
    }


    /// <summary>
    ///     回弹结束。有效时间为 1/3。
    /// </summary>
    public class AniEaseOutBack : AniEase
    {
        private readonly double p;

        public AniEaseOutBack(AniEasePower power = AniEasePower.Middle)
        {
            p = 3d - (double)power * 0.5d;
        }

        public override double GetValue(double t)
        {
            t = ModBase.MathClamp(t, 0d, 1d);
            return 1d - Math.Pow(1d - t, p) * Math.Cos(1.5d * Math.PI * t);
        }
    }


    // Car / 平滑-回弹
    /// <summary>
    ///     回弹开始，短平滑结束。
    /// </summary>
    public class AniEaseInCar : AniEase
    {
        private readonly AniEaseInout ease;

        public AniEaseInCar(double middle = 0.7d, AniEasePower power = AniEasePower.Middle)
        {
            ease = new AniEaseInout(new AniEaseInBack(power), new AniEaseOutFluent(power), middle);
        }

        public override double GetValue(double t)
        {
            return ease.GetValue(t);
        }
    }


    /// <summary>
    ///     短平滑开始，回弹结束。
    /// </summary>
    public class AniEaseOutCar : AniEase
    {
        private readonly AniEaseInout ease;

        public AniEaseOutCar(double middle = 0.3d, AniEasePower power = AniEasePower.Middle)
        {
            ease = new AniEaseInout(new AniEaseInFluent(power), new AniEaseOutBack(power), middle);
        }

        public override double GetValue(double t)
        {
            return ease.GetValue(t);
        }
    }


    // Elastic / 弹簧
    /// <summary>
    ///     弹簧开始。约在 60% 到达最小值。
    /// </summary>
    public class AniEaseInElastic : AniEase
    {
        private readonly int p; // 6~9

        public AniEaseInElastic(AniEasePower power = AniEasePower.Middle)
        {
            p = (int)power + 4;
        }

        public override double GetValue(double t)
        {
            t = ModBase.MathClamp(t, 0d, 1d);
            return Math.Pow(t, (p - 1) * 0.25d) * Math.Cos((p - 3.5d) * Math.PI * Math.Pow(1d - t, 1.5d));
        }
    }


    /// <summary>
    ///     弹簧结束。约在 40% 到达最大值。
    /// </summary>
    public class AniEaseOutElastic : AniEase
    {
        private readonly int p;

        public AniEaseOutElastic(AniEasePower power = AniEasePower.Middle)
        {
            p = (int)power + 4;
        }

        public override double GetValue(double t)
        {
            t = 1d - ModBase.MathClamp(t, 0d, 1d);
            return 1d - Math.Pow(t, (p - 1) * 0.25d) * Math.Cos((p - 3.5d) * Math.PI * Math.Pow(1d - t, 1.5d));
        }
    }




    /// <summary>
    ///     开始一个动画组。
    /// </summary>
    /// <param name="aniGroup">由 Aa 开头的函数初始化的 AniData 对象集合。</param>
    /// <param name="name">动画组的名称。如果重复会直接停止同名动画组。</param>
    public static void AniStart(IList aniGroup, string name = "", bool refreshTime = false)
    {
        if (refreshTime)
            aniLastTick = TimeUtils.GetTimeTick(); // 避免处理动画时已经造成了极大的延迟，导致动画突然结束
        // 添加到正在执行的动画组
        var newEntry = new AniGroupEntry
            { data = ModBase.GetFullList<AniData>(aniGroup), startTick = TimeUtils.GetTimeTick() };
        if (string.IsNullOrEmpty(name))
            name = newEntry.Uuid.ToString();
        else
            AniStop(name);
        aniGroups.TryAdd(name, newEntry);
    }


    /// <summary>
    ///     开始一个动画组。
    /// </summary>
    public static void AniStart(AniData aniGroup, string name = "", bool refreshTime = false)
    {
        AniStart(new List<AniData> { aniGroup }, name, refreshTime);
    }


    /// <summary>
    ///     直接停止一个动画组。
    /// </summary>
    /// <param name="name">需要停止的动画组的名称。</param>
    public static void AniStop(string name)
    {
        aniGroups.Remove(name, out _);
    }


    /// <summary>
    ///     获取动画是否正在进行中。
    /// </summary>
    public static bool AniIsRun(string name)
    {
        return aniGroups.ContainsKey(name);
    }

}
