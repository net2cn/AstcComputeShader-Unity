using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

[StructLayout(LayoutKind.Sequential)]
struct AstcHeader
{
    int magic;
    byte blockDimX;
    byte blockDimY;
    byte blockDimZ;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    byte[] xSize;           // x-size = xsize[0] + xsize[1] + xsize[2]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    byte[] ySize;           // x-size, y-size and z-size are given in texels;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    byte[] zSize;			// block count is inferred

    public AstcHeader(int xDim, int yDim, int xSize, int ySize)
    {
        magic = 0x5CA1AB13;
        blockDimX = (byte)xDim;
        blockDimY = (byte)yDim;
        blockDimZ = 1;
        this.xSize = new byte[3];
        this.xSize[0] = (byte)(xSize & 0xff);
        this.xSize[1] = (byte)((xSize >> 8) & 0xff);
        this.xSize[2] = (byte)((xSize >> 16) & 0xff);
        this.ySize = new byte[3];
        this.ySize[0] = (byte)(ySize & 0xff);
        this.ySize[1] = (byte)((ySize >> 8) & 0xff);
        this.ySize[2] = (byte)((ySize >> 16) & 0xff);
        zSize = new byte[3];
        zSize[0] = 1;
        zSize[1] = 0;
        zSize[2] = 0;
    }
}

// kernel indices defined in ASTC_Encode.compute, named after Unity's TextureFormat enum for convenience.
public enum ComputeShaderFormatToIndex
{
    ASTC_RGB_4x4 = 0,
    ASTC_RGBA_4x4 = 1,
    ASTC_RGB_6x6 = 2,
    ASTC_RGBA_6x6 = 3,
}

public class Main : MonoBehaviour
{
    public ComputeShader shader = null;
    public ComputeShaderFormatToIndex kernel = ComputeShaderFormatToIndex.ASTC_RGBA_4x4;
    public Texture2D textureToCompress = null;
    public RawImage compressedTextureImage = null;
    public Text usedTimeText = null;

    public Texture2D test;

    public ComputeShader psnrShader = null;

    void Awake()
    {
        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError("Compute shaders are not supported in this system!");
            return;
        }

        if (textureToCompress == null)
        {
            Debug.LogError("Missing reference textureToEncode!");
            return;
        }

        if (shader == null)
        {
            Debug.LogError("Missing reference shader!");
            return;
        }

        Debug.Log(textureToCompress.format);

        // Compress
        var compressSw = new System.Diagnostics.Stopwatch();
        compressSw.Start();
        var compressedTexture = GpuAstcCompress(kernel, textureToCompress);
        compressSw.Stop();
        compressedTextureImage.texture = compressedTexture;
        Debug.Log($"compress used time: {compressSw.ElapsedTicks / 10000f}ms");
        Debug.Log(compressedTexture.format);

        // GPU PSNR
        var psnrSw = new System.Diagnostics.Stopwatch();
        psnrSw.Start();
        var psnr = GpuCalculatePsnr(textureToCompress, compressedTexture);
        Debug.Log($"gpu psnr: {psnr}");
        psnrSw.Stop();
        Debug.Log($"gpu psnr used time: {psnrSw.ElapsedTicks / 10000f}ms");

#if UNITY_EDITOR
        // CPU PSNR
        var cpuPsnrSw = new System.Diagnostics.Stopwatch();
        cpuPsnrSw.Start();
        var cpuPsnr = CpuCalculatePsnr(textureToCompress, compressedTexture);
        Debug.Log($"cpu psnr: {cpuPsnr}");
        cpuPsnrSw.Stop();
        Debug.Log($"cpu psnr used time: {cpuPsnrSw.ElapsedTicks / 10000f}ms");
#endif

        if (usedTimeText != null)
        {
            usedTimeText.text = $"Used time: {(compressSw.ElapsedTicks + psnrSw.ElapsedTicks) / 10000f}ms\n" +
                $"Compression: {compressSw.ElapsedTicks / 10000f}ms\n" +
                $"PSNR Calc: {psnrSw.ElapsedTicks / 10000f}ms\n" +
                $"Before mem: {textureToCompress.GetRawTextureData().Length / 1024f}KB\n" +
                $"After mem: {compressedTexture.GetRawTextureData().Length / 1024f}KB\n" +
                $"PSNR: {psnr}";
        }
    }

    Texture2D GpuAstcCompress(ComputeShaderFormatToIndex format, Texture2D source)
    {
        string formatName = format.ToString();
        int kernel = (int)format;

        int dimSize = int.Parse(formatName[formatName.Length - 1].ToString());
        int texWidth = source.width;
        int texHeight = source.height;
        int xBlockNum = (texWidth + dimSize - 1) / dimSize;
        int yBlockNum = (texHeight + dimSize - 1) / dimSize;
        int totalBlockNum = xBlockNum * yBlockNum;

        int groupSize = 8 * 8;
        int groupNum = (totalBlockNum + groupSize - 1) / groupSize;
        int groupNumX = (texWidth + dimSize - 1) / dimSize;
        int groupNumY = (groupNum + groupNumX - 1) / groupNumX;

        var buffer = new ComputeBuffer(totalBlockNum, 16);
        shader.SetTexture(kernel, "InTexture", source);
        shader.SetInt("InTexelWidth", texWidth);
        shader.SetInt("InTexelHeight", texHeight);
        shader.SetInt("InGroupNumX", groupNumX);

        shader.SetBuffer(kernel, "OutBuffer", buffer);
        shader.Dispatch(kernel, groupNumX, groupNumY, 1);

        // read encoded data from GPU
        byte[] bytes = new byte[Marshal.SizeOf(typeof(AstcHeader)) + buffer.count * buffer.stride];
        var header = StructureToByteArray(new AstcHeader(dimSize, dimSize, texWidth, texHeight));
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        buffer.GetData(bytes, header.Length, 0, buffer.count * buffer.stride);

#if UNITY_EDITOR
        using (var fs = new FileStream("test.astc", FileMode.Create))
        {
            fs.Write(bytes, 0, bytes.Length);
        }
#endif
        // decode
        var compressedTexture = new Texture2D(texWidth, texHeight, (TextureFormat)Enum.Parse(typeof(TextureFormat), formatName), false);
        compressedTexture.LoadRawTextureData(bytes);
        compressedTexture.Apply();

        buffer.Dispose();

        return compressedTexture;
    }

    float GpuCalculatePsnr(Texture2D source, Texture2D target)
    {
        if (source == null || target == null || source.texelSize != target.texelSize)
        {
            return float.MinValue;
        }

        int texWidth = source.width;
        int texHeight = source.height;
        int texSize = texWidth * texHeight;

        // psnr one pass
        int reduceKernel = psnrShader.FindKernel("OnePassPsnr");

        psnrShader.SetTexture(reduceKernel, "OriginalTexture", source);
        psnrShader.SetTexture(reduceKernel, "CompareTexture", target);

        psnrShader.SetInt("InTexelWidth", texWidth);
        psnrShader.SetInt("InTexelHeight", texHeight);

        psnrShader.SetInt("ThreadCount", 512);

        var outBuffer = new ComputeBuffer(texWidth * texHeight / 1024, sizeof(float) * 4);
        psnrShader.SetBuffer(reduceKernel, "OutBuffer", outBuffer);

        for (int i = texSize; i >= 1024;)
        {
            if (i == texSize)
            {
                psnrShader.SetBool("FirstStep", true);
            }
            i /= 1024;
            psnrShader.Dispatch(reduceKernel, i, 1, 1);
            psnrShader.SetBool("FirstStep", false);

            if (i < 1024 && i != 1)
            {
                psnrShader.SetInt("ThreadCount", i);
                psnrShader.Dispatch(reduceKernel, 1, 1, 1);
            }
        }

        // fetch from GPU
        float[] result = new float[4];
        outBuffer.GetData(result);
        outBuffer.Dispose();

        // avg psnr
        float mse = (result[0] + result[1] + result[2] + result[3]) / texWidth / texHeight / 4f;
        float psnr = (float)(10f * Math.Log10(1f * 1f / mse));

        return psnr;
    }

    float CpuCalculatePsnr(Texture2D source, Texture2D target)
    {
        if (source == null || target == null || source.texelSize != target.texelSize)
        {
            return float.MinValue;
        }

        var sourcePx = source.GetPixels();
        var targetPx = target.GetPixels();

        Color accumulate = new Color(0, 0, 0, 0);
        for (int i = 0; i < sourcePx.Length; i++)
        {
            var diff = sourcePx[i] - targetPx[i];
            accumulate += diff * diff;
        }
        Debug.Log($"{accumulate.r}, {accumulate.g}, {accumulate.b}, {accumulate.a}");
        Debug.Log((accumulate.r + accumulate.g + accumulate.b + accumulate.a));
        float mse = (accumulate.r + accumulate.g + accumulate.b + accumulate.b) / sourcePx.Length / 4f;
        Debug.Log(mse);
        float psnr = (float)(10f * Math.Log10(1f * 1f / mse));

        return psnr;
    }

    byte[] StructureToByteArray(object obj)
    {
        int len = Marshal.SizeOf(obj);
        byte[] arr = new byte[len];

        IntPtr ptr = Marshal.AllocHGlobal(len);
        Marshal.StructureToPtr(obj, ptr, true);
        Marshal.Copy(ptr, arr, 0, len);
        Marshal.FreeHGlobal(ptr);

        return arr;
    }
}
