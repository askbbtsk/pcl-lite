using System;

namespace PCL;

/// <summary>
/// 更新源自己能说清原因的失败：仓库没配置、没发 Release、tag 不是合法 SemVer 等等。
/// <para>
/// 这类错误的 <see cref="Exception.Message"/> 是写给用户看的，可以直接弹出来；
/// 而网络异常、JSON 解析失败之类的原因对用户毫无意义，仍然走统一的提示文案。
/// <see cref="UpdatesWrapperModel"/> 会把源的异常吞掉换成一句笼统的「获取版本信息失败」，
/// 所以这里靠类型把原因带出去，由 <see cref="UpdateManager"/> 决定要不要展示。
/// </para>
/// </summary>
public class UpdateSourceException : Exception
{
    public UpdateSourceException(string message) : base(message)
    {
    }

    public UpdateSourceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
