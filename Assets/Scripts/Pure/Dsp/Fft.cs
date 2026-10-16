using System;

namespace MusicGame.Pure.Dsp
{
    /// <summary>
    /// 原地 radix-2 FFT（Cooley-Tukey，迭代实现，含位反转置换）。
    /// 长度必须为 2 的幂；输入 re/im 等长，输出未归一化的复频谱。
    /// 纯 C# 实现，供 Unity 与 dotnet 自检工程共同编译。
    /// </summary>
    public class Fft
    {
        // 旋转因子缓存（按长度缓存一份，避免每次 FFT 重复算三角函数）
        private static readonly object TwiddleLock = new object();
        private static double[] _twiddleCos;
        private static double[] _twiddleSin;
        private static int _twiddleSize;

        /// <summary>对 re/im 执行原地正变换（DIT，位反转后逐级蝶形）。</summary>
        public static void Forward(double[] re, double[] im)
        {
            if (re == null) throw new ArgumentNullException(nameof(re));
            if (im == null) throw new ArgumentNullException(nameof(im));
            int n = re.Length;
            if (im.Length != n) throw new ArgumentException("re and im must have the same length.");
            if (n <= 1) return; // 长度 1 视为平凡情形
            if ((n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two: " + n);

            // ---- 位反转置换 ----
            int bits = 0;
            while ((1 << bits) < n) bits++;
            for (int i = 1; i < n; i++)
            {
                int j = ReverseBits(i, bits);
                if (j > i)
                {
                    double tr = re[i]; re[i] = re[j]; re[j] = tr;
                    double ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            // ---- 逐级蝶形 ----
            double[] cosT, sinT;
            GetTwiddles(n, out cosT, out sinT);
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                int stride = n / size; // 该级旋转因子在表中的采样步长
                for (int baseIdx = 0; baseIdx < n; baseIdx += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        double wr = cosT[k * stride];
                        double wi = sinT[k * stride];
                        int a = baseIdx + k;
                        int b = a + half;
                        double tr = wr * re[b] - wi * im[b];
                        double ti = wr * im[b] + wi * re[b];
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }

        private static int ReverseBits(int value, int bits)
        {
            int result = 0;
            for (int b = 0; b < bits; b++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }
            return result;
        }

        // 取长度 n 的 e^{-2πik/n} 表（k = 0..n/2-1），构建过程加锁保证可见性
        private static void GetTwiddles(int n, out double[] cosT, out double[] sinT)
        {
            lock (TwiddleLock)
            {
                if (_twiddleSize != n || _twiddleCos == null || _twiddleSin == null)
                {
                    int half = n >> 1;
                    var cosTable = new double[half];
                    var sinTable = new double[half];
                    double baseAngle = -2.0 * Math.PI / n;
                    for (int k = 0; k < half; k++)
                    {
                        cosTable[k] = Math.Cos(baseAngle * k);
                        sinTable[k] = Math.Sin(baseAngle * k);
                    }
                    _twiddleCos = cosTable;
                    _twiddleSin = sinTable;
                    _twiddleSize = n;
                }
                cosT = _twiddleCos;
                sinT = _twiddleSin;
            }
        }
    }
}
