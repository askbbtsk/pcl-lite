using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class TahoeGlassMathTests
{
    [TestMethod]
    public void CircleDistanceFieldMatchesVideoEighthPower()
    {
        const double radius = 100, strength = .05;
        for (int i = 0; i <= 1000; i++)
        {
            double distance = radius * i / 1000;
            double signedDistance = distance - radius;
            double expected = -strength * Math.Pow(distance / radius, 8);
            double actual = -strength * TahoeGlassMath.EighthPower(1 + signedDistance / radius);
            Assert.AreEqual(expected, actual, 1e-14);
        }
    }

    [TestMethod]
    public void EighthPowerIsBoundedAndKeepsTheCenterStill()
    {
        Assert.AreEqual(0, TahoeGlassMath.EighthPower(double.NaN));
        Assert.AreEqual(0, TahoeGlassMath.EighthPower(-1));
        Assert.AreEqual(0, TahoeGlassMath.EighthPower(0));
        Assert.AreEqual(1d / 256, TahoeGlassMath.EighthPower(.5));
        Assert.AreEqual(1, TahoeGlassMath.EighthPower(2));
    }

    [TestMethod]
    public void RadiusFitsCapsuleAndRejectsInvalidParameters()
    {
        Assert.AreEqual(14, TahoeGlassMath.NormalizeRadius(1000, new Size(180, 28)));
        Assert.AreEqual(0, TahoeGlassMath.NormalizeRadius(-8, new Size(180, 28)));
        Assert.AreEqual(24, TahoeGlassMath.NormalizeRadius(double.NaN, new Size(300, 100)));
        Assert.AreEqual(0, TahoeGlassMath.NormalizeRadius(24, new Size(0, 100)));
        Assert.AreEqual(.05, TahoeGlassMath.FiniteClamp(double.PositiveInfinity, 0, .15, .05));
        Assert.AreEqual(.15, TahoeGlassMath.FiniteClamp(9, 0, .15, .05));
    }

    [TestMethod]
    public void RefractionDirectionRotatesAndMirrorsWithSurface()
    {
        var rotation = Matrix.Identity;
        rotation.Rotate(90);
        var direction = TahoeGlassMath.SourceDirection(rotation, new Vector(1, 0));
        Assert.AreEqual(0, direction.X, 1e-12);
        Assert.AreEqual(1, direction.Y, 1e-12);
        var mirror = new Matrix(-2, 0, 0, 3, 400, 300);
        direction = TahoeGlassMath.SourceDirection(mirror, new Vector(1, 0));
        Assert.AreEqual(-1, direction.X);
        Assert.AreEqual(0, direction.Y);
    }

    [TestMethod]
    public void SamplingCropIncludesUvRefractionAndBlurPadding()
    {
        var crop = TahoeGlassMath.SamplingBounds(new Matrix(1, 0, 0, 1, 200, 150),
            new Size(100, 60), new Size(1000, 800), .05, 2);
        Assert.AreEqual(new Rect(142, 102, 216, 156), crop);
        crop = TahoeGlassMath.SamplingBounds(Matrix.Identity, new Size(100, 60), new Size(1000, 800), .05, 2);
        Assert.AreEqual(new Rect(0, 0, 158, 108), crop);
        Assert.IsTrue(TahoeGlassMath.SamplingBounds(new Matrix(0, 0, 0, 0, 0, 0),
            new Size(100, 60), new Size(1000, 800), .05, 2).IsEmpty);
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    [DataRow(1.5d)]
    [DataRow(2d)]
    public void DipSamplingCoordinatesAreIndependentOfDeviceScale(double deviceScale)
    {
        var sourceDip = new Size(1000, 800);
        var panelDip = new Point(100, 50);
        var transform = new Matrix(1.2, .2, -.2, 1.2, 80, 120);
        Point sourcePoint = transform.Transform(panelDip);
        var pixelPoint = new Point(sourcePoint.X * deviceScale, sourcePoint.Y * deviceScale);
        Assert.AreEqual(sourcePoint.X / sourceDip.Width, pixelPoint.X / (sourceDip.Width * deviceScale), 1e-14);
        Assert.AreEqual(sourcePoint.Y / sourceDip.Height, pixelPoint.Y / (sourceDip.Height * deviceScale), 1e-14);
    }
}
