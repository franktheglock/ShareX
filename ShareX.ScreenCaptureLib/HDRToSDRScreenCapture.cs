#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using SharpGen.Runtime;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    internal static class HDRToSDRScreenCapture
    {
        private static readonly FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        private static readonly Format[] captureFormats =
        [
            Format.R16G16B16A16_Float,
            Format.R10G10B10A2_UNorm,
            Format.B8G8R8A8_UNorm
        ];

        public static Bitmap Capture(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return null;
            }

            Bitmap result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            long capturedArea = 0;

            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    IDXGIAdapter1 adapter = null;

                    if (factory.EnumAdapters1(adapterIndex, out adapter).Failure)
                    {
                        break;
                    }

                    using (adapter)
                    {
                        if (CaptureAdapter(adapter, rect, result, ref capturedArea))
                        {
                            continue;
                        }
                    }
                }

                return capturedArea >= (long)rect.Width * rect.Height ? result : null;
            }
            catch (Exception e)
            {
                result.Dispose();
                DebugHelper.WriteException(e, "HDR to SDR screen capture failed.");
                return null;
            }
        }

        private static bool CaptureAdapter(IDXGIAdapter1 adapter, Rectangle requestedRect, Bitmap result, ref long capturedArea)
        {
            ID3D11Device device = null;
            ID3D11DeviceContext context = null;

            using IDXGIAdapter baseAdapter = adapter.QueryInterface<IDXGIAdapter>();
            Result createDeviceResult = D3D11.D3D11CreateDevice(baseAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels, out device, out context);

            if (createDeviceResult.Failure)
            {
                device?.Dispose();
                context?.Dispose();
                return false;
            }

            using (device)
            using (context)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    IDXGIOutput output = null;

                    if (adapter.EnumOutputs(outputIndex, out output).Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        OutputDescription outputDescription = output.Description;

                        if (!outputDescription.AttachedToDesktop || outputDescription.Rotation != ModeRotation.Identity)
                        {
                            continue;
                        }

                        Rectangle outputRect = outputDescription.DesktopCoordinates;
                        Rectangle intersection = Rectangle.Intersect(requestedRect, outputRect);

                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            continue;
                        }

                        using IDXGIOutput5 output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();

                        if (output5 == null)
                        {
                            continue;
                        }

                        if (CaptureOutput(output5, device, context, outputRect, requestedRect, intersection, result))
                        {
                            capturedArea += (long)intersection.Width * intersection.Height;
                        }
                    }
                }
            }

            return true;
        }

        private static bool CaptureOutput(IDXGIOutput5 output, ID3D11Device device, ID3D11DeviceContext context, Rectangle outputRect, Rectangle requestedRect,
            Rectangle intersection, Bitmap result)
        {
            IDXGIOutputDuplication duplication = null;
            IDXGIResource desktopResource = null;
            bool frameAcquired = false;

            try
            {
                duplication = output.DuplicateOutput1(device, captureFormats);

                OutduplFrameInfo frameInfo = default;
                Result acquireResult = duplication.AcquireNextFrame(250, out frameInfo, out desktopResource);

                if (acquireResult.Failure || desktopResource == null)
                {
                    return false;
                }

                frameAcquired = true;

                using ID3D11Texture2D desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription description = desktopTexture.Description;
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.MiscFlags = ResourceOptionFlags.None;

                using ID3D11Texture2D stagingTexture = device.CreateTexture2D(in description);
                context.CopyResource(stagingTexture, desktopTexture);

                MappedSubresource mappedResource = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                try
                {
                    CopyMappedTexture(mappedResource, description.Format, outputRect, requestedRect, intersection, result);
                }
                finally
                {
                    context.Unmap(stagingTexture, 0);
                }

                return true;
            }
            finally
            {
                if (frameAcquired)
                {
                    duplication?.ReleaseFrame();
                }

                desktopResource?.Dispose();
                duplication?.Dispose();
            }
        }

        private static unsafe void CopyMappedTexture(MappedSubresource mappedResource, Format format, Rectangle outputRect, Rectangle requestedRect,
            Rectangle intersection, Bitmap result)
        {
            BitmapData bitmapData = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                byte* sourceBase = (byte*)mappedResource.DataPointer;
                byte* destinationBase = (byte*)bitmapData.Scan0;
                int sourceX = intersection.X - outputRect.X;
                int sourceY = intersection.Y - outputRect.Y;
                int destinationX = intersection.X - requestedRect.X;
                int destinationY = intersection.Y - requestedRect.Y;

                for (int y = 0; y < intersection.Height; y++)
                {
                    byte* source = sourceBase + ((sourceY + y) * mappedResource.RowPitch);
                    byte* destination = destinationBase + ((destinationY + y) * bitmapData.Stride) + (destinationX * 4);

                    switch (format)
                    {
                        case Format.R16G16B16A16_Float:
                            CopyRgbaHalfRow(source, destination, sourceX, intersection.Width);
                            break;
                        case Format.R10G10B10A2_UNorm:
                            CopyRgb10Row(source, destination, sourceX, intersection.Width);
                            break;
                        case Format.B8G8R8A8_UNorm:
                            CopyBgraRow(source, destination, sourceX, intersection.Width);
                            break;
                    }
                }
            }
            finally
            {
                result.UnlockBits(bitmapData);
            }
        }

        private static unsafe void CopyRgbaHalfRow(byte* source, byte* destination, int sourceX, int width)
        {
            ushort* halfSource = (ushort*)source + (sourceX * 4);

            for (int x = 0; x < width; x++)
            {
                float r = Math.Max(0, (float)BitConverter.UInt16BitsToHalf(halfSource[0]));
                float g = Math.Max(0, (float)BitConverter.UInt16BitsToHalf(halfSource[1]));
                float b = Math.Max(0, (float)BitConverter.UInt16BitsToHalf(halfSource[2]));
                destination[0] = ToSdrByte(b);
                destination[1] = ToSdrByte(g);
                destination[2] = ToSdrByte(r);
                destination[3] = 255;

                halfSource += 4;
                destination += 4;
            }
        }

        private static unsafe void CopyRgb10Row(byte* source, byte* destination, int sourceX, int width)
        {
            uint* sourcePixel = (uint*)source + sourceX;

            for (int x = 0; x < width; x++)
            {
                uint pixel = *sourcePixel++;
                float r = (pixel & 0x3ff) / 1023f;
                float g = ((pixel >> 10) & 0x3ff) / 1023f;
                float b = ((pixel >> 20) & 0x3ff) / 1023f;
                destination[0] = ToByte(b);
                destination[1] = ToByte(g);
                destination[2] = ToByte(r);
                destination[3] = 255;
                destination += 4;
            }
        }

        private static unsafe void CopyBgraRow(byte* source, byte* destination, int sourceX, int width)
        {
            byte* sourcePixel = source + (sourceX * 4);

            for (int x = 0; x < width; x++)
            {
                destination[0] = sourcePixel[0];
                destination[1] = sourcePixel[1];
                destination[2] = sourcePixel[2];
                destination[3] = 255;

                sourcePixel += 4;
                destination += 4;
            }
        }

        private static byte ToSdrByte(float linearValue)
        {
            float toneMapped = ToneMapACES(linearValue);
            float encoded = toneMapped <= 0.0031308f ? toneMapped * 12.92f : (1.055f * MathF.Pow(toneMapped, 1f / 2.4f)) - 0.055f;

            return ToByte(encoded);
        }

        private static float ToneMapACES(float value)
        {
            const float a = 2.51f;
            const float b = 0.03f;
            const float c = 2.43f;
            const float d = 0.59f;
            const float e = 0.14f;

            return Math.Clamp((value * ((a * value) + b)) / ((value * ((c * value) + d)) + e), 0, 1);
        }

        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }
    }
}
