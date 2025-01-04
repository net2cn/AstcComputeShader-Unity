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
public enum ComputeShaderKernelIndex
{
    ASTC_RGB_4x4 = 0,
    ASTC_RGBA_4x4 = 1,
    ASTC_RGB_6x6 = 2,
    ASTC_RGBA_6x6 = 3,
}

public class Main : MonoBehaviour
{
    public ComputeShader shader = null;
    public ComputeShaderKernelIndex kernel = ComputeShaderKernelIndex.ASTC_RGBA_4x4;
    public Texture2D textureToCompress = null;
    public RawImage compressedTextureImage = null;
    public Text usedTimeText = null;

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

        var modeName = kernel.ToString();
        int dimSize = int.Parse(modeName[modeName.Length - 1].ToString());
        int texWidth = textureToCompress.width;
        int texHeight = textureToCompress.height;
        int xBlockNum = (texWidth + dimSize - 1) / dimSize;
        int yBlockNum = (texHeight + dimSize - 1) / dimSize;
        int totalBlockNum = xBlockNum * yBlockNum;

        int groupSize = 8 * 8;
        int groupNum = (totalBlockNum + groupSize - 1) / groupSize;
        int groupNumX = (texWidth + dimSize - 1) / dimSize;
        int groupNumY = (groupNum + groupNumX - 1) / groupNumX;

        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        
        shader.SetTexture((int)kernel, "InTexture", textureToCompress);
        shader.SetInt("InTexelWidth", texWidth);
        shader.SetInt("InTexelHeight", texHeight);
        shader.SetInt("InGroupNumX", groupNumX);

        var buffer = new ComputeBuffer(totalBlockNum, 16);
        shader.SetBuffer((int)kernel, "OutBuffer", buffer);
        
        shader.Dispatch((int)kernel, groupNumX, groupNumY, 1);

        var header = new AstcHeader(dimSize, dimSize, texWidth, texHeight);
        var compressedTexture = new Texture2D(texWidth, texHeight, (TextureFormat)Enum.Parse(typeof(TextureFormat), modeName), false);
        using (var ms = new MemoryStream())
        {
            var headerByte = StructureToByteArray(header);
            ms.Write(headerByte, 0, headerByte.Length);
            var bodyByte = new byte[buffer.count * buffer.stride];
            buffer.GetData(bodyByte);
            ms.Write(bodyByte, 0, bodyByte.Length);

#if UNITY_EDITOR
            using (var fs = new FileStream("test.astc", FileMode.Create))
            {
                ms.WriteTo(fs);
            }
#endif

            compressedTexture.LoadRawTextureData(ms.ToArray());
            compressedTexture.Apply();
        }

        compressedTextureImage.texture = compressedTexture;
        buffer.Dispose();
        stopwatch.Stop();

        Debug.Log($"Used time: {stopwatch.ElapsedMilliseconds}ms");
        Debug.Log(compressedTexture.format);

        if (usedTimeText != null)
        {
            usedTimeText.text = $"Used time: {stopwatch.ElapsedMilliseconds}ms";
        }
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
