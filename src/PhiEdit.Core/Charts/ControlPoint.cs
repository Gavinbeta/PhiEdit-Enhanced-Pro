namespace PhiEdit.Core.Charts;

/// <summary>
/// 判定线控制曲线上的点。distance 为 x，数值为 y，easing 为该段使用的缓动编号。
/// 对应参考源码 scene.mjs 的 ControlCurve。
/// </summary>
public sealed record ControlPoint(double X, double Y, int Easing = 1);
