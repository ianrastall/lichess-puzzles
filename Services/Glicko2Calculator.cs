using System;
namespace Lichess_Puzzles.Services;

public static class Glicko2Calculator
{
    private const double Tau = 0.5; // system constant; smaller -> less volatility changes
    private const double Scale = 173.7178; // Glicko scale factor
    private const double DefaultVolatility = 0.06;

    /// <summary>
    /// Applies a single-result Glicko-2 update and returns the new rating tuple.
    /// </summary>
    public static (double rating, double rd, double volatility) Update(
        double rating, double rd, double volatility,
        double opponentRating, double opponentRd,
        double score)
    {
        var mu = (rating - 1500) / Scale;
        var phi = rd / Scale;

        var muJ = (opponentRating - 1500) / Scale;
        var phiJ = opponentRd / Scale;

        var gPhi = G(phiJ);
        var eVal = E(mu, muJ, phiJ);

        var v = 1.0 / (gPhi * gPhi * eVal * (1 - eVal));
        var delta = v * gPhi * (score - eVal);

        var a = Math.Log(volatility * volatility);
        var epsilon = 0.000001;

        double f(double x)
        {
            var expX = Math.Exp(x);
            var top = expX * (delta * delta - phi * phi - v - expX);
            var bottom = 2 * Math.Pow(phi * phi + v + expX, 2);
            return top / bottom - (x - a) / (Tau * Tau);
        }

        double A = a;
        double B;
        if (delta * delta > phi * phi + v)
        {
            B = Math.Log(delta * delta - phi * phi - v);
        }
        else
        {
            int k = 1;
            while (f(a - k * Tau) < 0)
                k++;
            B = a - k * Tau;
        }

        double fA = f(A);
        double fB = f(B);

        while (Math.Abs(B - A) > epsilon)
        {
            var C = A + (A - B) * fA / (fB - fA);
            var fC = f(C);

            if (fC * fB < 0)
            {
                A = B;
                fA = fB;
            }
            else
            {
                fA /= 2.0;
            }

            B = C;
            fB = fC;
        }

        var newSigma = Math.Exp(A / 2.0);
        var phiStar = Math.Sqrt(phi * phi + newSigma * newSigma);
        var newPhi = 1.0 / Math.Sqrt(1.0 / (phiStar * phiStar) + 1.0 / v);
        var newMu = mu + newPhi * newPhi * gPhi * (score - eVal);

        var newRating = newMu * Scale + 1500;
        var newRd = newPhi * Scale;

        newRd = Math.Clamp(newRd, 30, 350);
        newSigma = Math.Max(newSigma, 0.01);

        return (newRating, newRd, newSigma);
    }

    private static double G(double phi) => 1.0 / Math.Sqrt(1.0 + (3.0 * Math.Pow(Math.Log(10) / 400.0, 2) * phi * phi) / Math.PI / Math.PI);

    private static double E(double mu, double muJ, double phiJ) => 1.0 / (1.0 + Math.Exp(-G(phiJ) * (mu - muJ)));
}
