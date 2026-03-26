using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace MvCameraHelper
{
    #region 事件参数与委托

    /// <summary>
    /// 相机操作异常
    /// </summary>
    public class CameraException : Exception
    {
        public int ErrorCode { get; }
        public CameraException(string message, int errorCode)
            : base(FormatErrorMessage(message, errorCode))
        {
            ErrorCode = errorCode;
        }

        private static string FormatErrorMessage(string message, int errorCode)
        {
            if (errorCode == 0) return message;

            string errorMsg = message + ": Error = 0x" + errorCode.ToString("X");
            errorMsg += errorCode switch
            {
                MvError.MV_E_HANDLE => " (Error or invalid handle)",
                MvError.MV_E_SUPPORT => " (Not supported function)",
                MvError.MV_E_BUFOVER => " (Cache is full)",
                MvError.MV_E_CALLORDER => " (Function calling order error)",
                MvError.MV_E_PARAMETER => " (Incorrect parameter)",
                MvError.MV_E_RESOURCE => " (Applying resource failed)",
                MvError.MV_E_NODATA => " (No data)",
                MvError.MV_E_PRECONDITION => " (Precondition error, or running environment changed)",
                MvError.MV_E_VERSION => " (Version mismatches)",
                MvError.MV_E_NOENOUGH_BUF => " (Insufficient memory)",
                MvError.MV_E_UNKNOW => " (Unknown error)",
                MvError.MV_E_GC_GENERIC => " (General error)",
                MvError.MV_E_GC_ACCESS => " (Node accessing condition error)",
                MvError.MV_E_ACCESS_DENIED => " (No permission)",
                MvError.MV_E_BUSY => " (Device is busy, or network disconnected)",
                MvError.MV_E_NETER => " (Network error)",
                _ => ""
            };
            return errorMsg;
        }
    }

    /// <summary>
    /// 帧接收事件参数
    /// </summary>
    public class FrameReceivedEventArgs : EventArgs
    {
        public IFrameOut Frame { get; }
        public FrameReceivedEventArgs(IFrameOut frame) => Frame = frame;
    }

    /// <summary>
    /// 设备信息描述
    /// </summary>
    public class DeviceDescription
    {
        public int Index { get; set; }
        public string DisplayName { get; set; }
        public IDeviceInfo DeviceInfo { get; set; }
    }

    #endregion

    #region 通用相机操作基类（面阵/线扫共用）

    /// <summary>
    /// MvCamera 通用操作基类，封装面阵相机和线扫相机的公共功能。
    /// 包括: SDK初始化、设备枚举、打开/关闭、取流、触发、保存图像、像素格式等。
    /// </summary>
    public class MvCameraBase : IDisposable
    {
        #region 字段

        protected static readonly DeviceTLayerType DefaultTLayerType =
            DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice
            | DeviceTLayerType.MvGenTLGigEDevice | DeviceTLayerType.MvGenTLCXPDevice
            | DeviceTLayerType.MvGenTLCameraLinkDevice | DeviceTLayerType.MvGenTLXoFDevice;

        protected IDevice device;
        protected List<IDeviceInfo> deviceInfoList = new List<IDeviceInfo>();

        protected volatile bool isGrabbing;
        protected Thread receiveThread;

        protected IFrameOut frameForSave;
        protected readonly object saveImageLock = new object();

        private bool sdkInitialized;
        private bool disposed;

        #endregion

        #region 事件

        /// <summary>
        /// 每帧图像接收完成时触发
        /// </summary>
        public event EventHandler<FrameReceivedEventArgs> FrameReceived;

        /// <summary>
        /// 取流过程中出现异常时触发
        /// </summary>
        public event EventHandler<Exception> GrabException;

        #endregion

        #region 属性

        /// <summary>
        /// 底层设备对象，可用于直接访问 SDK 高级功能
        /// </summary>
        public IDevice Device => device;

        /// <summary>
        /// 当前是否正在取图
        /// </summary>
        public bool IsGrabbing => isGrabbing;

        /// <summary>
        /// 当前是否已打开设备
        /// </summary>
        public bool IsOpened => device != null;

        /// <summary>
        /// 最近缓存的帧（用于保存图像）
        /// </summary>
        public IFrameOut LastFrame
        {
            get { lock (saveImageLock) { return frameForSave; } }
        }

        #endregion

        #region SDK 初始化/释放

        /// <summary>
        /// 初始化 SDK（全局调用一次）
        /// </summary>
        public void InitializeSDK()
        {
            if (!sdkInitialized)
            {
                SDKSystem.Initialize();
                sdkInitialized = true;
            }
        }

        /// <summary>
        /// 释放 SDK（全局调用一次）
        /// </summary>
        public void FinalizeSDK()
        {
            if (sdkInitialized)
            {
                SDKSystem.Finalize();
                sdkInitialized = false;
            }
        }

        #endregion

        #region 设备枚举

        /// <summary>
        /// 枚举所有设备
        /// </summary>
        /// <param name="layerType">传输层类型，默认 null 使用全部类型</param>
        /// <returns>设备描述列表</returns>
        public List<DeviceDescription> EnumDevices(DeviceTLayerType? layerType = null)
        {
            var tlType = layerType ?? DefaultTLayerType;
            int result = DeviceEnumerator.EnumDevices(tlType, out deviceInfoList);
            if (result != MvError.MV_OK)
            {
                throw new CameraException("枚举设备失败", result);
            }

            var descriptions = new List<DeviceDescription>();
            for (int i = 0; i < deviceInfoList.Count; i++)
            {
                var info = deviceInfoList[i];
                string displayName = info.UserDefinedName != ""
                    ? $"{info.TLayerType}: {info.UserDefinedName} ({info.SerialNumber})"
                    : $"{info.TLayerType}: {info.ManufacturerName} {info.ModelName} ({info.SerialNumber})";

                descriptions.Add(new DeviceDescription
                {
                    Index = i,
                    DisplayName = displayName,
                    DeviceInfo = info
                });
            }
            return descriptions;
        }

        /// <summary>
        /// 获取枚举到的设备数量
        /// </summary>
        public int DeviceCount => deviceInfoList.Count;

        #endregion

        #region 打开/关闭设备

        /// <summary>
        /// 打开指定索引的设备
        /// </summary>
        /// <param name="index">设备在枚举列表中的索引</param>
        public void Open(int index)
        {
            if (index < 0 || index >= deviceInfoList.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "无效的设备索引");

            Open(deviceInfoList[index]);
        }

        /// <summary>
        /// 打开指定设备信息的设备
        /// </summary>
        public void Open(IDeviceInfo deviceInfo)
        {
            try
            {
                device = DeviceFactory.CreateDevice(deviceInfo);
            }
            catch (Exception ex)
            {
                throw new CameraException("创建设备失败: " + ex.Message, 0);
            }

            int result = device.Open();
            if (result != MvError.MV_OK)
            {
                device.Dispose();
                device = null;
                throw new CameraException("打开设备失败", result);
            }

            // GigE 设备自动设置最优包大小
            OptimizeGigEPacketSize();

            // 默认连续采集模式
            device.Parameters.SetEnumValueByString("AcquisitionMode", "Continuous");
            device.Parameters.SetEnumValueByString("TriggerMode", "Off");
        }

        /// <summary>
        /// 关闭设备并释放资源
        /// </summary>
        public void Close()
        {
            if (isGrabbing)
            {
                StopGrabbing();
            }

            if (device != null)
            {
                device.Close();
                device.Dispose();
                device = null;
            }

            lock (saveImageLock)
            {
                frameForSave?.Dispose();
                frameForSave = null;
            }
        }

        #endregion

        #region GigE 优化

        /// <summary>
        /// 探测并设置 GigE 设备最佳网络包大小
        /// </summary>
        protected void OptimizeGigEPacketSize()
        {
            if (device is IGigEDevice gigEDevice)
            {
                int result = gigEDevice.GetOptimalPacketSize(out int packetSize);
                if (result == MvError.MV_OK)
                {
                    device.Parameters.SetIntValue("GevSCPSPacketSize", packetSize);
                }
            }
        }

        #endregion

        #region 取流控制

        /// <summary>
        /// 开始取图（自动启动接收线程）
        /// </summary>
        /// <param name="displayHandle">用于 SDK 渲染的控件句柄（可选，传 IntPtr.Zero 则不渲染）</param>
        public void StartGrabbing(IntPtr displayHandle = default)
        {
            EnsureDeviceOpened();

            isGrabbing = true;
            receiveThread = new Thread(() => ReceiveThreadProcess(displayHandle))
            {
                IsBackground = true,
                Name = "MvCamera_ReceiveThread"
            };
            receiveThread.Start();

            int result = device.StreamGrabber.StartGrabbing();
            if (result != MvError.MV_OK)
            {
                isGrabbing = false;
                receiveThread.Join();
                throw new CameraException("开始取图失败", result);
            }
        }

        /// <summary>
        /// 停止取图
        /// </summary>
        public void StopGrabbing()
        {
            isGrabbing = false;
            receiveThread?.Join();

            if (device != null)
            {
                int result = device.StreamGrabber.StopGrabbing();
                if (result != MvError.MV_OK)
                {
                    throw new CameraException("停止取图失败", result);
                }
            }
        }

        /// <summary>
        /// 取流接收线程
        /// </summary>
        protected virtual void ReceiveThreadProcess(IntPtr displayHandle)
        {
            while (isGrabbing)
            {
                int result = device.StreamGrabber.GetImageBuffer(1000, out IFrameOut frameOut);
                if (result == MvError.MV_OK)
                {
                    try
                    {
                        // 缓存帧用于保存
                        lock (saveImageLock)
                        {
                            frameForSave?.Dispose();
                            frameForSave = frameOut.Clone() as IFrameOut;
                        }

                        // 渲染
                        if (displayHandle != IntPtr.Zero)
                        {
                            device.ImageRender.DisplayOneFrame(displayHandle, frameOut.Image);
                        }

                        // 触发事件
                        FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frameOut));
                    }
                    catch (Exception ex)
                    {
                        GrabException?.Invoke(this, ex);
                    }
                    finally
                    {
                        device.StreamGrabber.FreeImageBuffer(frameOut);
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        /// <summary>
        /// 主动获取单帧图像（不通过线程循环）
        /// </summary>
        /// <param name="timeoutMs">超时毫秒数</param>
        /// <returns>帧数据（调用者需负责 FreeImageBuffer）</returns>
        public IFrameOut GetOneFrame(int timeoutMs = 1000)
        {
            EnsureDeviceOpened();
            int result = device.StreamGrabber.GetImageBuffer(timeoutMs, out IFrameOut frameOut);
            if (result != MvError.MV_OK)
            {
                throw new CameraException("获取帧失败", result);
            }
            return frameOut;
        }

        /// <summary>
        /// 释放帧缓冲
        /// </summary>
        public void FreeImageBuffer(IFrameOut frame)
        {
            device?.StreamGrabber.FreeImageBuffer(frame);
        }

        #endregion

        #region 触发控制

        /// <summary>
        /// 设置触发模式
        /// </summary>
        /// <param name="isOn">true=触发模式, false=连续模式</param>
        public void SetTriggerMode(bool isOn)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetEnumValueByString("TriggerMode", isOn ? "On" : "Off");
            if (result != MvError.MV_OK)
            {
                throw new CameraException("设置触发模式失败", result);
            }
        }

        /// <summary>
        /// 获取当前触发模式是否开启
        /// </summary>
        public bool GetTriggerMode()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("TriggerMode", out IEnumValue enumValue);
            if (result == MvError.MV_OK)
            {
                return enumValue.CurEnumEntry.Symbolic == "On";
            }
            return false;
        }

        /// <summary>
        /// 设置触发源
        /// </summary>
        /// <param name="source">触发源名称，如 "Software"、"Line0" 等</param>
        public void SetTriggerSource(string source)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetEnumValueByString("TriggerSource", source);
            if (result != MvError.MV_OK)
            {
                throw new CameraException("设置触发源失败", result);
            }
        }

        /// <summary>
        /// 获取当前触发源
        /// </summary>
        public string GetTriggerSource()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("TriggerSource", out IEnumValue enumValue);
            return result == MvError.MV_OK ? enumValue.CurEnumEntry.Symbolic : "";
        }

        /// <summary>
        /// 获取支持的触发源列表
        /// </summary>
        public List<string> GetSupportedTriggerSources()
        {
            EnsureDeviceOpened();
            var sources = new List<string>();
            int result = device.Parameters.GetEnumValue("TriggerSource", out IEnumValue enumValue);
            if (result == MvError.MV_OK)
            {
                foreach (var entry in enumValue.SupportEnumEntries)
                {
                    sources.Add(entry.Symbolic);
                }
            }
            return sources;
        }

        /// <summary>
        /// 执行软触发
        /// </summary>
        public void TriggerSoftwareOnce()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetCommandValue("TriggerSoftware");
            if (result != MvError.MV_OK)
            {
                throw new CameraException("软触发执行失败", result);
            }
        }

        #endregion

        #region 图像保存

        /// <summary>
        /// 保存当前缓存帧为 BMP
        /// </summary>
        public string SaveBmp(string directory = ".")
        {
            return SaveImageInternal(new ImageFormatInfo { FormatType = ImageFormatType.Bmp }, directory);
        }

        /// <summary>
        /// 保存当前缓存帧为 JPEG
        /// </summary>
        /// <param name="quality">JPEG 质量 (1-100)，默认 80</param>
        public string SaveJpeg(int quality = 80, string directory = ".")
        {
            var info = new ImageFormatInfo
            {
                FormatType = ImageFormatType.Jpeg,
                JpegQuality = quality
            };
            return SaveImageInternal(info, directory);
        }

        /// <summary>
        /// 保存当前缓存帧为 PNG
        /// </summary>
        public string SavePng(string directory = ".")
        {
            return SaveImageInternal(new ImageFormatInfo { FormatType = ImageFormatType.Png }, directory);
        }

        /// <summary>
        /// 保存当前缓存帧为 TIFF
        /// </summary>
        public string SaveTiff(string directory = ".")
        {
            return SaveImageInternal(new ImageFormatInfo { FormatType = ImageFormatType.Tiff }, directory);
        }

        /// <summary>
        /// 通用保存图像方法
        /// </summary>
        /// <param name="formatInfo">格式信息</param>
        /// <param name="directory">保存目录</param>
        /// <returns>保存的文件路径</returns>
        protected string SaveImageInternal(ImageFormatInfo formatInfo, string directory)
        {
            lock (saveImageLock)
            {
                if (frameForSave == null)
                    throw new CameraException("没有可保存的图像帧", 0);

                string fileName = $"Image_w{frameForSave.Image.Width}_h{frameForSave.Image.Height}_fn{frameForSave.FrameNum}.{formatInfo.FormatType}";
                string filePath = System.IO.Path.Combine(directory, fileName);

                int result = device.ImageSaver.SaveImageToFile(filePath, frameForSave.Image, formatInfo, CFAMethod.Equilibrated);
                if (result != MvError.MV_OK)
                {
                    throw new CameraException("保存图像失败", result);
                }
                return filePath;
            }
        }

        /// <summary>
        /// 将指定帧保存到指定路径
        /// </summary>
        public void SaveFrameToFile(IFrameOut frame, string filePath, ImageFormatInfo formatInfo)
        {
            EnsureDeviceOpened();
            int result = device.ImageSaver.SaveImageToFile(filePath, frame.Image, formatInfo, CFAMethod.Equilibrated);
            if (result != MvError.MV_OK)
            {
                throw new CameraException("保存图像失败", result);
            }
        }

        #endregion

        #region 通用参数（面阵/线扫共用）

        /// <summary>
        /// 获取曝光时间（微秒）
        /// </summary>
        public double GetExposureTime()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetFloatValue("ExposureTime", out IFloatValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取曝光时间失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置曝光时间（微秒），自动关闭自动曝光
        /// </summary>
        public void SetExposureTime(float exposureTime)
        {
            EnsureDeviceOpened();
            device.Parameters.SetEnumValue("ExposureAuto", 0);
            int result = device.Parameters.SetFloatValue("ExposureTime", exposureTime);
            if (result != MvError.MV_OK)
                throw new CameraException("设置曝光时间失败", result);
        }

        /// <summary>
        /// 获取当前像素格式
        /// </summary>
        public string GetPixelFormat()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("PixelFormat", out IEnumValue val);
            return result == MvError.MV_OK ? val.CurEnumEntry.Symbolic : "";
        }

        /// <summary>
        /// 获取支持的像素格式列表
        /// </summary>
        public List<string> GetSupportedPixelFormats()
        {
            EnsureDeviceOpened();
            var formats = new List<string>();
            int result = device.Parameters.GetEnumValue("PixelFormat", out IEnumValue val);
            if (result == MvError.MV_OK)
            {
                foreach (var entry in val.SupportEnumEntries)
                {
                    formats.Add(entry.Symbolic);
                }
            }
            return formats;
        }

        /// <summary>
        /// 设置像素格式
        /// </summary>
        public void SetPixelFormat(string format)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetEnumValueByString("PixelFormat", format);
            if (result != MvError.MV_OK)
                throw new CameraException("设置像素格式失败", result);
        }

        /// <summary>
        /// 获取图像宽度
        /// </summary>
        public long GetWidth()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetIntValue("Width", out IIntValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取图像宽度失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 获取图像高度
        /// </summary>
        public long GetHeight()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetIntValue("Height", out IIntValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取图像高度失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置图像宽度
        /// </summary>
        public void SetWidth(long width)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetIntValue("Width", width);
            if (result != MvError.MV_OK)
                throw new CameraException("设置图像宽度失败", result);
        }

        /// <summary>
        /// 设置图像高度
        /// </summary>
        public void SetHeight(long height)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetIntValue("Height", height);
            if (result != MvError.MV_OK)
                throw new CameraException("设置图像高度失败", result);
        }

        #endregion

        #region 通用参数读写（高级）

        /// <summary>
        /// 读取整型参数
        /// </summary>
        public long GetIntParam(string paramName)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetIntValue(paramName, out IIntValue val);
            if (result != MvError.MV_OK)
                throw new CameraException($"获取参数 {paramName} 失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置整型参数
        /// </summary>
        public void SetIntParam(string paramName, long value)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetIntValue(paramName, value);
            if (result != MvError.MV_OK)
                throw new CameraException($"设置参数 {paramName} 失败", result);
        }

        /// <summary>
        /// 读取浮点参数
        /// </summary>
        public double GetFloatParam(string paramName)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetFloatValue(paramName, out IFloatValue val);
            if (result != MvError.MV_OK)
                throw new CameraException($"获取参数 {paramName} 失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置浮点参数
        /// </summary>
        public void SetFloatParam(string paramName, float value)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetFloatValue(paramName, value);
            if (result != MvError.MV_OK)
                throw new CameraException($"设置参数 {paramName} 失败", result);
        }

        /// <summary>
        /// 读取布尔参数
        /// </summary>
        public bool GetBoolParam(string paramName)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetBoolValue(paramName, out bool val);
            if (result != MvError.MV_OK)
                throw new CameraException($"获取参数 {paramName} 失败", result);
            return val;
        }

        /// <summary>
        /// 设置布尔参数
        /// </summary>
        public void SetBoolParam(string paramName, bool value)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetBoolValue(paramName, value);
            if (result != MvError.MV_OK)
                throw new CameraException($"设置参数 {paramName} 失败", result);
        }

        /// <summary>
        /// 读取枚举参数当前值
        /// </summary>
        public string GetEnumParam(string paramName)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue(paramName, out IEnumValue val);
            if (result != MvError.MV_OK)
                throw new CameraException($"获取参数 {paramName} 失败", result);
            return val.CurEnumEntry.Symbolic;
        }

        /// <summary>
        /// 设置枚举参数（按字符串）
        /// </summary>
        public void SetEnumParam(string paramName, string value)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetEnumValueByString(paramName, value);
            if (result != MvError.MV_OK)
                throw new CameraException($"设置参数 {paramName} 失败", result);
        }

        /// <summary>
        /// 设置枚举参数（按数值）
        /// </summary>
        public void SetEnumParam(string paramName, uint value)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetEnumValue(paramName, value);
            if (result != MvError.MV_OK)
                throw new CameraException($"设置参数 {paramName} 失败", result);
        }

        /// <summary>
        /// 获取枚举参数支持的所有选项
        /// </summary>
        public List<string> GetEnumParamEntries(string paramName)
        {
            EnsureDeviceOpened();
            var entries = new List<string>();
            int result = device.Parameters.GetEnumValue(paramName, out IEnumValue val);
            if (result == MvError.MV_OK)
            {
                foreach (var entry in val.SupportEnumEntries)
                {
                    entries.Add(entry.Symbolic);
                }
            }
            return entries;
        }

        /// <summary>
        /// 执行命令参数
        /// </summary>
        public void ExecuteCommand(string commandName)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetCommandValue(commandName);
            if (result != MvError.MV_OK)
                throw new CameraException($"执行命令 {commandName} 失败", result);
        }

        #endregion

        #region 辅助

        protected void EnsureDeviceOpened()
        {
            if (device == null)
                throw new InvalidOperationException("设备未打开");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    Close();
                }
                disposed = true;
            }
        }

        ~MvCameraBase()
        {
            Dispose(false);
        }

        #endregion
    }

    #endregion

    #region 面阵相机操作（AreaScan 专有功能）

    /// <summary>
    /// 面阵相机操作类，在通用功能基础上增加:
    /// 增益、帧率、录像功能。
    /// </summary>
    public class AreaScanCamera : MvCameraBase
    {
        private volatile bool isRecording;

        /// <summary>
        /// 当前是否正在录像
        /// </summary>
        public bool IsRecording => isRecording;

        #region 增益

        /// <summary>
        /// 获取增益值
        /// </summary>
        public double GetGain()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetFloatValue("Gain", out IFloatValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取增益失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置增益值，自动关闭自动增益
        /// </summary>
        public void SetGain(float gain)
        {
            EnsureDeviceOpened();
            device.Parameters.SetEnumValue("GainAuto", 0);
            int result = device.Parameters.SetFloatValue("Gain", gain);
            if (result != MvError.MV_OK)
                throw new CameraException("设置增益失败", result);
        }

        #endregion

        #region 帧率

        /// <summary>
        /// 获取当前实际帧率
        /// </summary>
        public double GetResultingFrameRate()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetFloatValue("ResultingFrameRate", out IFloatValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取帧率失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置采集帧率，自动启用帧率控制
        /// </summary>
        public void SetFrameRate(float frameRate)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetBoolValue("AcquisitionFrameRateEnable", true);
            if (result != MvError.MV_OK)
                throw new CameraException("启用帧率控制失败", result);

            result = device.Parameters.SetFloatValue("AcquisitionFrameRate", frameRate);
            if (result != MvError.MV_OK)
                throw new CameraException("设置帧率失败", result);
        }

        /// <summary>
        /// 获取帧率使能状态
        /// </summary>
        public bool GetFrameRateEnable()
        {
            EnsureDeviceOpened();
            device.Parameters.GetBoolValue("AcquisitionFrameRateEnable", out bool val);
            return val;
        }

        #endregion

        #region 录像

        /// <summary>
        /// 开始录像（AVI格式）
        /// </summary>
        /// <param name="filePath">录像文件路径</param>
        /// <param name="frameRate">帧率</param>
        /// <param name="bitRate">码率 (kbps)，默认 1000</param>
        public void StartRecord(string filePath, float frameRate, int bitRate = 1000)
        {
            EnsureDeviceOpened();
            if (!isGrabbing)
                throw new InvalidOperationException("请先开始取图再录像");

            int result;

            result = device.Parameters.GetIntValue("Width", out IIntValue widthVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取宽度失败", result);

            result = device.Parameters.GetIntValue("Height", out IIntValue heightVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取高度失败", result);

            result = device.Parameters.GetEnumValue("PixelFormat", out IEnumValue pixelVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取像素格式失败", result);

            RecordParam recordParam;
            recordParam.Width = (uint)widthVal.CurValue;
            recordParam.Height = (uint)heightVal.CurValue;
            recordParam.PixelType = (MvGvspPixelType)pixelVal.CurEnumEntry.Value;
            recordParam.FrameRate = frameRate;
            recordParam.BitRate = bitRate;
            recordParam.FormatType = VideoFormatType.AVI;

            result = device.VideoRecorder.StartRecord(filePath, recordParam);
            if (result != MvError.MV_OK)
                throw new CameraException("开始录像失败", result);

            isRecording = true;
        }

        /// <summary>
        /// 停止录像
        /// </summary>
        public void StopRecord()
        {
            if (!isRecording) return;

            EnsureDeviceOpened();
            int result = device.VideoRecorder.StopRecord();
            if (result != MvError.MV_OK)
                throw new CameraException("停止录像失败", result);

            isRecording = false;
        }

        /// <summary>
        /// 重写取流线程：增加录像帧输入
        /// </summary>
        protected override void ReceiveThreadProcess(IntPtr displayHandle)
        {
            while (isGrabbing)
            {
                int result = device.StreamGrabber.GetImageBuffer(1000, out IFrameOut frameOut);
                if (result == MvError.MV_OK)
                {
                    try
                    {
                        // 录像帧输入
                        if (isRecording)
                        {
                            device.VideoRecorder.InputOneFrame(frameOut.Image);
                        }

                        // 缓存帧
                        lock (saveImageLock)
                        {
                            frameForSave?.Dispose();
                            frameForSave = frameOut.Clone() as IFrameOut;
                        }

                        // 渲染
                        if (displayHandle != IntPtr.Zero)
                        {
                            device.ImageRender.DisplayOneFrame(displayHandle, frameOut.Image);
                        }

                        // 触发事件
                        FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frameOut));
                    }
                    catch (Exception ex)
                    {
                        GrabException?.Invoke(this, ex);
                    }
                    finally
                    {
                        device.StreamGrabber.FreeImageBuffer(frameOut);
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        // 重新声明事件以便在 override 方法中使用
        public new event EventHandler<FrameReceivedEventArgs> FrameReceived;
        public new event EventHandler<Exception> GrabException;

        /// <summary>
        /// 获取面阵相机全部参数快照
        /// </summary>
        public AreaScanParams GetAllParams()
        {
            EnsureDeviceOpened();
            var p = new AreaScanParams();

            try { p.ExposureTime = GetExposureTime(); } catch { }
            try { p.Gain = GetGain(); } catch { }
            try { p.FrameRate = GetResultingFrameRate(); } catch { }
            try { p.PixelFormat = GetPixelFormat(); } catch { }
            try { p.Width = GetWidth(); } catch { }
            try { p.Height = GetHeight(); } catch { }
            try { p.TriggerMode = GetTriggerMode(); } catch { }
            try { p.TriggerSource = GetTriggerSource(); } catch { }

            return p;
        }
    }

    /// <summary>
    /// 面阵相机参数快照
    /// </summary>
    public class AreaScanParams
    {
        public double ExposureTime { get; set; }
        public double Gain { get; set; }
        public double FrameRate { get; set; }
        public string PixelFormat { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public bool TriggerMode { get; set; }
        public string TriggerSource { get; set; }
    }

    #endregion

    #region 线扫相机操作（LineScan 专有功能）

    /// <summary>
    /// 线扫相机操作类，在通用功能基础上增加:
    /// 数字增益(DigitalShift)、模拟增益(PreampGain)、行频、HB模式、触发选择器。
    /// </summary>
    public class LineScanCamera : MvCameraBase
    {
        #region 数字增益 (DigitalShift)

        /// <summary>
        /// 获取数字增益
        /// </summary>
        public double GetDigitalShift()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetFloatValue("DigitalShift", out IFloatValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取数字增益失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置数字增益，自动启用 DigitalShiftEnable
        /// </summary>
        public void SetDigitalShift(float value)
        {
            EnsureDeviceOpened();
            device.Parameters.SetBoolValue("DigitalShiftEnable", true);
            int result = device.Parameters.SetFloatValue("DigitalShift", value);
            if (result != MvError.MV_OK)
                throw new CameraException("设置数字增益失败", result);
        }

        #endregion

        #region 模拟增益 (PreampGain)

        /// <summary>
        /// 获取当前模拟增益
        /// </summary>
        public string GetPreampGain()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("PreampGain", out IEnumValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取模拟增益失败", result);
            return val.CurEnumEntry.Symbolic;
        }

        /// <summary>
        /// 获取支持的模拟增益列表
        /// </summary>
        public List<string> GetSupportedPreampGains()
        {
            return GetEnumParamEntries("PreampGain");
        }

        /// <summary>
        /// 设置模拟增益
        /// </summary>
        public void SetPreampGain(string gainSymbolic)
        {
            EnsureDeviceOpened();
            // 需通过枚举值设置
            int result = device.Parameters.GetEnumValue("PreampGain", out IEnumValue enumVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取模拟增益参数失败", result);

            foreach (var entry in enumVal.SupportEnumEntries)
            {
                if (entry.Symbolic == gainSymbolic)
                {
                    result = device.Parameters.SetEnumValue("PreampGain", entry.Value);
                    if (result != MvError.MV_OK)
                        throw new CameraException("设置模拟增益失败", result);
                    return;
                }
            }
            throw new CameraException($"不支持的模拟增益: {gainSymbolic}", 0);
        }

        #endregion

        #region 行频

        /// <summary>
        /// 获取行频使能状态
        /// </summary>
        public bool GetAcquisitionLineRateEnable()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetBoolValue("AcquisitionLineRateEnable", out bool val);
            return result == MvError.MV_OK && val;
        }

        /// <summary>
        /// 设置行频使能
        /// </summary>
        public void SetAcquisitionLineRateEnable(bool enable)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetBoolValue("AcquisitionLineRateEnable", enable);
            if (result != MvError.MV_OK)
                throw new CameraException("设置行频使能失败", result);
        }

        /// <summary>
        /// 获取行频设定值
        /// </summary>
        public long GetAcquisitionLineRate()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetIntValue("AcquisitionLineRate", out IIntValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取行频失败", result);
            return val.CurValue;
        }

        /// <summary>
        /// 设置行频
        /// </summary>
        public void SetAcquisitionLineRate(long lineRate)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.SetIntValue("AcquisitionLineRate", lineRate);
            if (result != MvError.MV_OK)
                throw new CameraException("设置行频失败", result);
        }

        /// <summary>
        /// 获取实际行频
        /// </summary>
        public long GetResultingLineRate()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetIntValue("ResultingLineRate", out IIntValue val);
            if (result != MvError.MV_OK)
                throw new CameraException("获取实际行频失败", result);
            return val.CurValue;
        }

        #endregion

        #region HB 模式 (ImageCompressionMode)

        /// <summary>
        /// 获取当前 HB 压缩模式
        /// </summary>
        public string GetImageCompressionMode()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("ImageCompressionMode", out IEnumValue val);
            if (result != MvError.MV_OK) return "";
            return val.CurEnumEntry.Symbolic;
        }

        /// <summary>
        /// 获取支持的 HB 压缩模式列表
        /// </summary>
        public List<string> GetSupportedCompressionModes()
        {
            return GetEnumParamEntries("ImageCompressionMode");
        }

        /// <summary>
        /// 设置 HB 压缩模式
        /// </summary>
        public void SetImageCompressionMode(string mode)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("ImageCompressionMode", out IEnumValue enumVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取压缩模式参数失败", result);

            foreach (var entry in enumVal.SupportEnumEntries)
            {
                if (entry.Symbolic == mode)
                {
                    result = device.Parameters.SetEnumValue("ImageCompressionMode", entry.Value);
                    if (result != MvError.MV_OK)
                        throw new CameraException("设置压缩模式失败", result);
                    return;
                }
            }
            throw new CameraException($"不支持的压缩模式: {mode}", 0);
        }

        #endregion

        #region 触发选择器 (TriggerSelector)

        /// <summary>
        /// 获取当前触发选择器
        /// </summary>
        public string GetTriggerSelector()
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("TriggerSelector", out IEnumValue val);
            if (result != MvError.MV_OK) return "";
            return val.CurEnumEntry.Symbolic;
        }

        /// <summary>
        /// 获取支持的触发选择器列表
        /// </summary>
        public List<string> GetSupportedTriggerSelectors()
        {
            return GetEnumParamEntries("TriggerSelector");
        }

        /// <summary>
        /// 设置触发选择器
        /// </summary>
        public void SetTriggerSelector(string selector)
        {
            EnsureDeviceOpened();
            int result = device.Parameters.GetEnumValue("TriggerSelector", out IEnumValue enumVal);
            if (result != MvError.MV_OK)
                throw new CameraException("获取触发选择器失败", result);

            foreach (var entry in enumVal.SupportEnumEntries)
            {
                if (entry.Symbolic == selector)
                {
                    result = device.Parameters.SetEnumValue("TriggerSelector", entry.Value);
                    if (result != MvError.MV_OK)
                        throw new CameraException("设置触发选择器失败", result);
                    return;
                }
            }
            throw new CameraException($"不支持的触发选择器: {selector}", 0);
        }

        #endregion

        /// <summary>
        /// 获取线扫相机全部参数快照
        /// </summary>
        public LineScanParams GetAllParams()
        {
            EnsureDeviceOpened();
            var p = new LineScanParams();

            try { p.ExposureTime = GetExposureTime(); } catch { }
            try { p.DigitalShift = GetDigitalShift(); } catch { }
            try { p.PreampGain = GetPreampGain(); } catch { }
            try { p.AcquisitionLineRate = GetAcquisitionLineRate(); } catch { }
            try { p.ResultingLineRate = GetResultingLineRate(); } catch { }
            try { p.AcquisitionLineRateEnable = GetAcquisitionLineRateEnable(); } catch { }
            try { p.PixelFormat = GetPixelFormat(); } catch { }
            try { p.ImageCompressionMode = GetImageCompressionMode(); } catch { }
            try { p.TriggerMode = GetTriggerMode(); } catch { }
            try { p.TriggerSource = GetTriggerSource(); } catch { }
            try { p.TriggerSelector = GetTriggerSelector(); } catch { }
            try { p.Width = GetWidth(); } catch { }
            try { p.Height = GetHeight(); } catch { }

            return p;
        }
    }

    /// <summary>
    /// 线扫相机参数快照
    /// </summary>
    public class LineScanParams
    {
        public double ExposureTime { get; set; }
        public double DigitalShift { get; set; }
        public string PreampGain { get; set; }
        public long AcquisitionLineRate { get; set; }
        public long ResultingLineRate { get; set; }
        public bool AcquisitionLineRateEnable { get; set; }
        public string PixelFormat { get; set; }
        public string ImageCompressionMode { get; set; }
        public bool TriggerMode { get; set; }
        public string TriggerSource { get; set; }
        public string TriggerSelector { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
    }

    #endregion
}