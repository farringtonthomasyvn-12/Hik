using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace InfraredDemo
{
    public partial class InfraredDemo : Form
    {
        List<IDeviceInfo> deviceInfoList = new List<IDeviceInfo>();
        IDevice device = null;

        bool isOpen = false;                // ch:是否打开设备 | en:Whether to open device
        bool isGrabbing = false;            // ch:是否开始抓图 | en:Whether to start grabbing
        IntPtr displayHandle = IntPtr.Zero; // ch:用于显示图像的控件句柄 | en:Handle of the image display control
        Thread m_hReceiveThread = null;

        // ch:判断用户自定义像素格式 | en:Determine custom pixel format
        public const Int32 CUSTOMER_PIXEL_FORMAT = unchecked((Int32)0x80000000);
        public const Int32 TEMP_CHUNK_ID_TEST = unchecked((Int32)0x00510002);                    // ch:测温区域信息 | en:Temp Measurement region information
        public const Int32 TEMP_CHUNK_ID_ALARM = unchecked((Int32)0x00510003);                 // ch:测温警告信息 | en:Temp Measurement alarm information
        public const Int32 TEMP_CHUNK_ID_RAW_DATA = unchecked((Int32)0x00510004);               // ch:全屏灰度数据 | en:Full ScreenRaw data
        public const Int32 TEMP_CHUNK_ID_FULL_SCREEN_DATA = unchecked((Int32)0x00510005);       // ch:全屏温度数据 | en:Full Screen Temperature data
        public const Int32 TEMP_CHUNK_ID_MIN_MAX_TEMP = unchecked((Int32)0x00510006);           // ch:最低温最高温 | en:Min Temp & Max Temp
        public const Int32 TEMP_CHUNK_ID_OSD_INFO = unchecked((Int32)0x00510007);               // ch:OSD相关参数 | en:OSD Related parameters

        public const Int32 TEMP_ROI_TYPE_POINT = 0;
        public const Int32 TEMP_ROI_TYPE_LINE = 1;
        public const Int32 TEMP_ROI_TYPE_POLYGON = 2;
        public const Int32 TEMP_ROI_TYPE_CIRCLE = 3;

        public const Int32 TEMP_ALARM_LEVER_PRE = 0;        // ch:报警等级-预警 | en；Alarm level-Early warning
        public const Int32 TEMP_ALARM_LEVER_WARN = 1;       // ch:报警等级-警告 | en:Alarm level-Warning
        public const Int32 TEMP_ALARM_LEVER_NORMAL = 2;     // ch:报警等级-正常 | en:Alarm level-Normal
        public const Int32 TEMP_ALARM_LEVER_RECOVER = 3;    // ch:报警等级-解除等级 | en:Alarm level-Recover
        public const Int32 TEMP_ALARM_TYPE_MAX = 0;         // ch:报警类型-最大值 | en:Alarm type-Maximum Temperature Difference
        public const Int32 TEMP_ALARM_TYPE_MIN = 1;         // ch:报警类型-最小值 | en:Alarm type-Minimum Temperature Difference
        public const Int32 TEMP_ALARM_TYPE_AVG = 2;         // ch:报警类型-平均值 | en:Alarm type-Average Temperature Difference
        public const Int32 TEMP_ALARM_TYPE_DIFFER = 3;      // ch:报警类型-差异值 | en:Alarm type-Variation Temperature Difference

        public InfraredDemo()
        {
            InitializeComponent();
            EnableControls(false);
            this.Load += new EventHandler(this.InfraredDemo_Load);
            displayHandle = pbDisplay.Handle;
        }

        private void InfraredDemo_Load(object sender, EventArgs e)
        {
            // ch: 初始化 SDK | en: Initialize SDK
            SDKSystem.Initialize();
        }

        public static FormRegionSetting RegionSettingForm = null;   // 区域设置界面
        public static FormAlarmSetting AlarmSettingForm = null;    // 告警设置界面
        
        public const Int32  TEMP_REGION_COUNT = 22;                // ch:全部区域个数 | en:Number of all region
        // ch:点 | en：Point
        public struct IFR_POINT
        {
            public UInt32 x;
            public UInt32 y;
        }


        // ch:多边形区域 | en:Polygon region
        public struct IFR_POLYGON
        {
            public UInt32 pointNum;              // <ch:多边形实际顶点数 | en:Point of Polygon region
            public UInt32 circleRadius;          // <ch:测温区域圆半径 | en:CircleRadius of Temperature Measurement region
            public IFR_POINT circlepoint;              // <ch:测温区域的圆心点 | en:Circlepoint of Temperature Measurement region

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = TEMP_REGION_COUNT)]
            public IFR_POINT[] pointList;               // <ch:顶点坐标 | en:PointList
        }


        // ch:单个区域测温结果 | en:Temp Measurement Result of Temperature Region
        public struct IFR_OUTCOME_INFO
        {
            public Byte enable;             /// <ch:是否启用：0-否,1-是 | en:Enable: 0-disable,1-Enable
            public Byte regionId;           /// <ch:区域ID | en:RegionId

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 34)]
            public Byte[] reserved;

            public UInt32 regiontype;         /// <ch:区域类型 0：点 1：线 2：多边形  3:圆 | en:Region Type 0:point 1:Line 2:Polygon

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public Byte[] name;                      /// <ch:区域名称 | en:Region Name

            public UInt32 emissionRate;       /// <ch:发射率: [1，100] | en:Emissivity
            public Int32 minTmp;             /// <ch:最低温度: [-400, 10000]，单位0.1℃ | en:Minimum Temperature: [-400, 10000]，Unit:0.1℃
            public Int32 maxTmp;             /// <ch:最高温度: [-400, 10000]，单位0.1℃ | en:Maximum Temperature: [-400, 10000]，Unit:0.1℃
            public Int32 avrTmp;             /// <ch:平均温度: [-400, 10000]，单位0.1℃ | en:Average Temperature: [-400, 10000]，Unit:0.1℃
            public Int32 diffTmp;            /// <ch:温差： [0, 10400]，单位0.1℃ | en:Temperature variation:[0, 10400],Unit:0.1℃         

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public IFR_POINT[] points;                       /// <ch:保存测试结果中的最高温和最低温坐标，数组下标: 0-最高温，1-最低温 | en:Save Maximum Temperature  and Minimum Temperature coordinates in the test results,Array subscript:0-Maximum Temperature,1-Minimum Temperature

            public IFR_POLYGON polygon;            /// <ch:多边形区域 | en:Polygon region
        }

        public struct IFR_TM_REGION_NUM
        {
            public Byte pointNum;     // ch:点测温个数，最大10个 | en:Number of Point Temperature region,Max:Ten
            public Byte boxNum;       // ch:框测温个数，最大10个 | en:Number of polygon Temperature region,Max:Ten
            public Byte lineNum;      // ch:线测温个数，最多1条 | en:Number of Line Temperature region,Max:One
            public Byte circleNum;    // ch:圆测温个数，最多1个 | en:Number of Circle Temperature region,Max:One
            public Byte total;        // ch:上者之和 | en:All of the above

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public Byte[] res;
        }
        // ch:温度结果列表 | en:Temperature Results List
        public struct IFR_OUTCOME_LIST
        {
            public IFR_TM_REGION_NUM regionNum;                     /// <ch:有效测温区域数量 | en:Number of effective temperature measurement region

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public Byte[] res;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = TEMP_REGION_COUNT)]
            public IFR_OUTCOME_INFO[] ifrOutcome;
        }

        // ch:报警上传信息结构体 | en::Uploading information structure of Alarm
        public struct IFR_ALARM_INFO
        {
            public Byte regionId;                     /// <ch:框序号 | en:RegionId
            public Byte alarmkey;                     /// <ch:当前区域报警是否开启开关，上层默认配置开启 | en:Whether the current region alarm is on or off, the upper layer is on by default
            public Byte alarmRule;                    /// <ch:规则:0-大于 1-小于 | en:Rule:0-greater than 1-Less than
            public Byte reserved0;                    /// <ch:预留 | en:Reserve
            public UInt32 regiontype;                    /// <ch:区域类型 0：点 1：线 2：多边形 3:圆 | en:Regiontype 0：point 1：Line 2：Polygon 3:Circle
            public UInt32 alarmType;                     /// <ch:报警类型:0-最高温报警,1-最低温度，2-平均温度,3-温差 | en:AlarmType:0-Maximum Temperature alarm,1-Minimum Temperature,2-Average Temperature,3-Temperature variation
            public UInt32 alarmLevel;                    /// <ch:报警级别：0-预警，1-报警，2-正常，3-解除预警 | en:Alarm Level:0-Early waining,2-Normal,3-Cancel early warning

            public Int32 measureTmpData;                         /// <ch:测量值，单位0.1℃ | en:Temperature Measurements,Unit0.1℃
            public Int32 ruleTmpData;                            /// <ch:规则设定值，单位0.1℃ | en:Rule Settings，Unit0.1℃
            public IFR_POLYGON polygon;                        /// <ch:区域位置 | en:Region Location

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public IFR_POINT[] points;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public Byte[] reserved1;
        }


        // ch:区域温差报警上传信息结构体 | en:Uploading information structure of  Multi Temperature Region Alarm
        public struct IFR_DIFF_ALARM_INFO
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public Byte[] regionSet;

            public Byte alarmkey;                     /// <ch:当前区域报警是否开启开关 | en:Whether the current region alarm is on
            public Byte reserved0;                    /// <ch:预留 | en:Reserve
            public UInt32 alarmType;                     /// <ch:报警类型:0-最高温报警,1-最低温度，2-平均温度,3-温差 | en:AlarmType:0-Maximum Temperature alarm,1-Minimum Temperature,2-Average Temperature,3-Temperature variation
            public Byte alarmRule;                    /// <ch:规则:0-大于 1-小于 | en:Rule:0-greater than 1-Less than

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public Byte[] reserved1;

            public UInt32 alarmLevel;                    /// <ch:报警级别：0-预警，1-报警，2-正常，3-解除预警 | en:Alarm Level:0-Early waining,2-Normal,3-Cancel early warning

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public UInt32[] measureTmpData;

            public Int32 ruleTmpData;                            /// <ch:规则设定值，单位0.1℃ | en:Rule Settings，Unit0.1℃                             

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public Byte[] reserved2;
        }


        public struct IFR_ALARM_UPLOAD_INFO
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = TEMP_REGION_COUNT)]
            public IFR_ALARM_INFO[] alarmOutcome;            /// ch:单区域报警结果 | en:Temperature Region Alarm result

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public IFR_DIFF_ALARM_INFO[] alarmDiffOutcome;   /// ch:区域间温差报警结果 | en: Multi Temperature Region Alarm result    
        }

        // ch:全屏温度信息 | en:Full Screen Temperature information
        public struct IFR_FULL_SCREEN_MAX_MIN_INFO
        {
            public UInt32 nMaxTemp;
            public UInt32 nMinTemp;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public Byte[] reserved;
        }

        // ch:测温区域显示规则 | en:Temperature region Display Rule
        public struct IRF_REGION_DISP_INFO
        {
            public UInt32 regionDispIndex;                    /**< ch:区域显示规则索引号 | en:Region Display rule Index number */
            public UInt32 regionDispEnable;                   /**< ch:测温区域显示使能 | en: Temp Measurement region Display Enable */
            public UInt32 regionMaxTempDispEnable;            /**< ch:区域最大温度显示使能 | en:Region Maximum Temperature Display Enable */
            public UInt32 regionMinTempDispEnable;            /**< ch:区域最小温度显示使能 | en:Region Minimum  Temperature Display Enable*/
            public UInt32 regionAvgTempDispEnable;            /**< ch:区域平均温度显示使能 | en:Region Average Temperature Display Enable*/
            public UInt32 regionAlarmDispEnable;              /**< ch:区域告警状态显示使能 | en:Region Alarm Temperature Display Enable*/
        }

        // ch:区域测温图像叠加特征信息结构体 | en:Information Structure of superimposed feature  of region temperature measurement image
        public struct IRF_OSD_INFO
        {
            public UInt32 legendDisplayEnable;    /**< ch:图例（温度条）是否使能 1 使能 0 不使能 | en:Legend: 1:Enable 0:Disable*/
            public UInt32 osdProcessor;           /**< ch:红外相机OSD叠加控制器选择 0 无叠加 1 相机叠加 2 客户端叠加 | en:Over Screen Display Processor: 0:None 1:Camera 2:Client*/

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = TEMP_REGION_COUNT)]
            public IRF_REGION_DISP_INFO[] regionDispRules;   /// ch:区域间温差报警结果 | en: Multi Temperature Region Alarm result  
        } 


        // ch:显示错误信息 | en:Show error message
        private void ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                errorMsg = csMessage;
            }
            else
            {
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            switch (nErrorNum)
            {
                case MvError.MV_E_HANDLE: errorMsg += "Error or invalid handle "; break;
                case MvError.MV_E_SUPPORT: errorMsg += "Not supported function "; break;
                case MvError.MV_E_BUFOVER: errorMsg += "Cache is full "; break;
                case MvError.MV_E_CALLORDER: errorMsg += "Function calling order error "; break;
                case MvError.MV_E_PARAMETER: errorMsg += "Incorrect parameter "; break;
                case MvError.MV_E_RESOURCE: errorMsg += "Applying resource failed "; break;
                case MvError.MV_E_NODATA: errorMsg += "No data "; break;
                case MvError.MV_E_PRECONDITION: errorMsg += "Precondition error, or running environment changed "; break;
                case MvError.MV_E_VERSION: errorMsg += "Version mismatches "; break;
                case MvError.MV_E_NOENOUGH_BUF: errorMsg += "Insufficient memory "; break;
                case MvError.MV_E_ABNORMAL_IMAGE: errorMsg += "Abnormal image, maybe incomplete image because of lost packet "; break;
                case MvError.MV_E_UNKNOW: errorMsg += "Unknown error "; break;
                case MvError.MV_E_GC_GENERIC: errorMsg += "General error "; break;
                case MvError.MV_E_GC_ACCESS: errorMsg += "Node accessing condition error "; break;
                case MvError.MV_E_ACCESS_DENIED: errorMsg += "No permission "; break;
                case MvError.MV_E_BUSY: errorMsg += "Device is busy, or network disconnected "; break;
                case MvError.MV_E_NETER: errorMsg += "Network error "; break;
            }

            MessageBox.Show(errorMsg, "PROMPT");
        }

        private void bnEnum_Click(object sender, EventArgs e)
        {
            DeviceListAcq();
            EnableControls(true);
        }

        private void DeviceListAcq()
        {
            // ch:创建设备列表 | en:Create Device List
            cbDeviceList.Items.Clear();
            int result = DeviceEnumerator.EnumDevices(DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, out deviceInfoList);
            if (0 != result)
            {
                ShowErrorMsg("Enumerate devices fail!", 0);
                return;
            }

            // ch:在窗体列表中显示设备名 | en:Display device name in the form list
            for (int i = 0; i < deviceInfoList.Count; i++)
            {
                IDeviceInfo deviceInfo = deviceInfoList[i];
                if (deviceInfo.UserDefinedName != "")
                {
                    cbDeviceList.Items.Add(deviceInfo.TLayerType.ToString() + ": " + deviceInfo.UserDefinedName + " (" + deviceInfo.SerialNumber + ")");
                }
                else
                {
                    cbDeviceList.Items.Add(deviceInfo.TLayerType.ToString() + ": " + deviceInfo.ManufacturerName + " " + deviceInfo.ModelName + " (" + deviceInfo.SerialNumber + ")");
                }
            }

            // ch:选择第一项 | en:Select the first item
            if (deviceInfoList.Count != 0)
            {
                cbDeviceList.SelectedIndex = 0;
            }
        }

        private void bnOpen_Click(object sender, EventArgs e)
        {
            if (deviceInfoList.Count == 0 || cbDeviceList.SelectedIndex == -1)
            {
                ShowErrorMsg("No device, please select", 0);
                return;
            }

            // ch:获取选择的设备信息 | en:Get selected device information
            IDeviceInfo deviceInfo = deviceInfoList[cbDeviceList.SelectedIndex];

            try
            {
                // ch:打开设备 | en:Open device
                device = DeviceFactory.CreateDevice(deviceInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Create Device fail!" + ex.Message);
                return;
            }

            int result = device.Open();
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Open Device fail!", result);
                return;
            }

            // ch:设置采集连续模式 | en:Set Continues Acquisition Mode
            device.Parameters.SetEnumValueByString("AcquisitionMode", "Continuous");
            device.Parameters.SetEnumValueByString("TriggerMode", "Off");

            isOpen = true;
            EnableControls(true);
            bnGetParameter_Click(null, null);
        }

        private void EnableControls(bool bIsCameraReady)
        {
            bnOpen.Enabled = (isOpen ? false : (bIsCameraReady ? true : false));
            bnClose.Enabled = ((isOpen && bIsCameraReady) ? true : false);
            bnStartGrab.Enabled = ((isGrabbing && bIsCameraReady) ? false : (isOpen ? true : false));
            bnStopGrab.Enabled = (isGrabbing ? true : false);
            cbPixelFormat.Enabled = ((isGrabbing && bIsCameraReady) ? false : (isOpen ? true : false));
            cbDisplaySource.Enabled = (isOpen ? true : false);
            cbLegendCheck.Enabled = (isOpen ? true : false);
            cbRegionSelect.Enabled = (isOpen ? true : false);
            bnRegionSetting.Enabled = (isOpen ? true : false);
            bnWarningSetting.Enabled = (isOpen ? true : false);
            teTransmissivity.Enabled = (isOpen ? true : false);
            teTargetDistance.Enabled = (isOpen ? true : false);
            teEmissivity.Enabled = (isOpen ? true : false);
            cbMeasureRange.Enabled = (isOpen ? true : false);
            bnGetParameter.Enabled = (isOpen ? true : false);
            bnSetParameter.Enabled = (isOpen ? true : false);
            cbPaletteMode.Enabled = (isOpen ? true : false);
            cbExportModeCheck.Enabled = (isOpen ? true : false);
        }

        private int ReadEnumIntoCombo(string strKey, ref ComboBox ctrlComboBox)
        {
            IEnumValue enumValue;
            int result = device.Parameters.GetEnumValue(strKey, out enumValue);
            if (MvError.MV_OK != result)
            {
                return result;
            }
            ctrlComboBox.Items.Clear();
            for (int i = 0; i < enumValue.SupportedNum; ++i)
            {
                ctrlComboBox.Items.Add(enumValue.SupportEnumEntries[i].Symbolic);
                if (enumValue.CurEnumEntry.Value == enumValue.SupportEnumEntries[i].Value)
                {
                    ctrlComboBox.SelectedIndex = i;
                }
            }
            return MvError.MV_OK;
        }

        private void bnGetParameter_Click(object sender, EventArgs e)
        {
            int result = ReadEnumIntoCombo("PixelFormat", ref cbPixelFormat);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get PixelFormat Fail!", result);
                return;
            }

            result = ReadEnumIntoCombo("OverScreenDisplayProcessor", ref cbDisplaySource);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get OverScreenDisplayProcessor Fail!", result);
                return;
            }

            result = ReadEnumIntoCombo("PalettesMode", ref cbPaletteMode);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get PalettesMode Fail!", result);
                return;
            }

            bool boolValue = false;
            result = device.Parameters.GetBoolValue("LegendDisplayEnable", out boolValue);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get LegendDisplayEnable Fail!", result);
                return;
            }
            else
            {
                cbLegendCheck.Checked = boolValue;
            }

            result = device.Parameters.GetBoolValue("MtExpertMode", out boolValue);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get MtExpertMode Fail!", result);
                return;
            }
            else
            {
                cbExportModeCheck.Checked = boolValue;
            }

            result = ReadEnumIntoCombo("TempRegionSelector", ref cbRegionSelect);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get TempRegionSelector Fail!", result);
                return;
            }

            result = ReadEnumIntoCombo("TempMeasurementRange", ref cbMeasureRange);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get TempMeasurementRange Fail!", result);
                return;
            }

            IIntValue intValue;
            result = device.Parameters.GetIntValue("AtmosphericTransmissivity", out intValue);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get AtmosphericTransmissivity Fail!", result);
            }
            else
            {
                teTransmissivity.Text = intValue.CurValue.ToString();
            }

            IFloatValue floatValue;
            result = device.Parameters.GetFloatValue("TargetDistance", out floatValue);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get TargetDistance Fail!", result);
                return;
            }
            else
            {
                teTargetDistance.Text = floatValue.CurValue.ToString();
            }

            result = device.Parameters.GetFloatValue("FullScreenEmissivity", out floatValue);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("Get FullScreenEmissivity!", result);
                return;
            }
            else
            {
                teEmissivity.Text = floatValue.CurValue.ToString();
            }
        }

        private void ReceiveThreadProcess()
        {
            IFrameOut frameOut;
            int result = MvError.MV_OK;
            using (StreamWriter writer = new StreamWriter("InfraredLog.txt"))
            {
                while (isGrabbing)
                {
                    // ch:获取图像 | en:Get image
                    result = device.StreamGrabber.GetImageBuffer(1000, out frameOut);
                    if (result == MvError.MV_OK)
                    {
                        // ch:显示图像 | en:Display image
                        device.ImageRender.DisplayOneFrame(displayHandle, frameOut.Image);


                        // 解析chunckdata数据
                        IFR_OUTCOME_LIST stOutComeList = new IFR_OUTCOME_LIST();
                        IFR_ALARM_UPLOAD_INFO stAlarmInfoList = new IFR_ALARM_UPLOAD_INFO();
                        IFR_FULL_SCREEN_MAX_MIN_INFO stFullScreenMaxMin = new IFR_FULL_SCREEN_MAX_MIN_INFO();
                        IRF_OSD_INFO stOsdInfo = new IRF_OSD_INFO();

                        // Chunk信息
                        IChunkInfo chunkInfo = frameOut.ChunkInfo;
                        foreach (var item in chunkInfo)
                        {
                            
                            if (TEMP_CHUNK_ID_TEST == item.ChunkID &&
                                Marshal.SizeOf(stOutComeList) == item.Length)
                            {
                                stOutComeList = (IFR_OUTCOME_LIST)Marshal.PtrToStructure(item.DataPtr, typeof(IFR_OUTCOME_LIST));
                            }
                            else if (TEMP_CHUNK_ID_ALARM == item.ChunkID &&
                                    Marshal.SizeOf(stAlarmInfoList) == item.Length)
                            {
                                stAlarmInfoList = (IFR_ALARM_UPLOAD_INFO)Marshal.PtrToStructure(item.DataPtr, typeof(IFR_ALARM_UPLOAD_INFO));
                            }
                            else if (TEMP_CHUNK_ID_MIN_MAX_TEMP == item.ChunkID &&
                                    Marshal.SizeOf(stFullScreenMaxMin) == item.Length)
                            {
                                stFullScreenMaxMin = (IFR_FULL_SCREEN_MAX_MIN_INFO)Marshal.PtrToStructure(item.DataPtr, typeof(IFR_FULL_SCREEN_MAX_MIN_INFO));
                            }
                            else if (TEMP_CHUNK_ID_OSD_INFO == item.ChunkID &&
                                    Marshal.SizeOf(stOsdInfo) == item.Length)
                            {
                                stOsdInfo = (IRF_OSD_INFO)Marshal.PtrToStructure(item.DataPtr, typeof(IRF_OSD_INFO));
                            }
                        }



                        writer.WriteLine("************Beginning Output Test Temperature Info, Frame Number:{0}************", frameOut.FrameNum);

                        IRF_REGION_DISP_INFO stRegionDispRules = new IRF_REGION_DISP_INFO();
                        int nRegionDispRulesSize = Marshal.SizeOf(stRegionDispRules);

                        for (int i = 0; i < TEMP_REGION_COUNT; ++i)
                        {
                            if (stOsdInfo.regionDispRules == null)
                            {
                                continue;
                            }
                            stRegionDispRules = stOsdInfo.regionDispRules[i];
                            //stRegionDispRules = (IRF_REGION_DISP_INFO)Marshal.PtrToStructure(stOsdInfo.regionDispRules[i], typeof(IRF_REGION_DISP_INFO));
                            if (stRegionDispRules.regionDispEnable.Equals(0))
                            {
                                continue;
                            }
                            if (stAlarmInfoList.alarmOutcome == null)
                            {
                                continue;
                            }
                            //deal with the alarm region.
                            IFR_ALARM_INFO stAlarmInfo = stAlarmInfoList.alarmOutcome[i];
                            if (stAlarmInfo.alarmkey > 0 && TEMP_ALARM_LEVER_WARN == stAlarmInfo.alarmLevel &&
                                (false == stRegionDispRules.regionAlarmDispEnable.Equals(0)))
                            {
                                IFR_POINT stPoint0 = stAlarmInfo.points[0];
                                IFR_POINT stPoint1 = stAlarmInfo.points[1];

                                if (TEMP_ALARM_TYPE_MAX == stAlarmInfo.alarmType)
                                {

                                    writer.WriteLine("RegionID: {0}; Alarm Max Temp : {1}; PointX : {2}; PointY : {3}",
                                    stAlarmInfo.regionId, 0.1 * stAlarmInfo.measureTmpData, stPoint0.x, stPoint0.y);

                                }
                                else if (TEMP_ALARM_TYPE_MIN == stAlarmInfo.alarmType)
                                {

                                    writer.WriteLine("RegionID: {0}; Alarm Min Temp : {1}; PointX : {2}; PointY :{3}",
                                    stAlarmInfo.regionId, 0.1 * stAlarmInfo.measureTmpData, stPoint1.x, stPoint1.y);
                                }
                                else if (TEMP_ALARM_TYPE_AVG == stAlarmInfo.alarmType)
                                {

                                    writer.WriteLine("RegionID: {0}; Alarm Avg Temp : {1}; PointX :{2}; PointY : {3}",
                                    stAlarmInfo.regionId, 0.1 * stAlarmInfo.measureTmpData, stPoint0.x, stPoint0.y);
                                }
                                else
                                {
                                    writer.WriteLine("RegionID: {0}; Alarm Differ Temp : {1}; PointX : {2}; PointY : {3}",
                                    stAlarmInfo.regionId, 0.1 * stAlarmInfo.measureTmpData, stPoint0.x, stPoint0.y);
                                }

                                continue;
                            }

                            //deal with the enable region. 
                            IFR_OUTCOME_INFO stOutCome = stOutComeList.ifrOutcome[i];
                            if (stOutCome.enable.Equals(0))
                            {
                                continue;
                            }

                            IFR_POINT stOutComePoint0 = stOutCome.points[0];
                            IFR_POINT stOutComePoint1 = stOutCome.points[1];

                            if (TEMP_ROI_TYPE_POINT == stOutCome.regiontype)
                            {
                                if (false == stRegionDispRules.regionAvgTempDispEnable.Equals(0))
                                {
                                    writer.WriteLine("RegionID: {0}; Avg Temp : {1}; PointX : {2}; PointY :{3}",
                                    stOutCome.regionId, 0.1 * stOutCome.avrTmp, stOutComePoint0.x, stOutComePoint0.y);
                                }
                            }
                            else
                            {
                                //均值显示在最大值的后面
                                //均值显示在最大值的后面
                                if (false == stRegionDispRules.regionAvgTempDispEnable.Equals(0))
                                {
                                    writer.WriteLine("RegionID: {0}; Avg Temp :{1}; PointX :  {2}; PointY :{3}",
                                    stOutCome.regionId, 0.1 * stOutCome.avrTmp, stOutComePoint0.x, stOutComePoint0.y);

                                }

                                if (false == stRegionDispRules.regionMaxTempDispEnable.Equals(0))
                                {
                                    writer.WriteLine("RegionID: {0}; Max Temp : {1}; PointX : {2}; PointY : {3}",
                                    stOutCome.regionId, 0.1 * stOutCome.maxTmp, stOutComePoint0.x, stOutComePoint0.y);
                                }

                                if (false == stRegionDispRules.regionMinTempDispEnable.Equals(0))
                                {
                                    writer.WriteLine("RegionID: {0}; Min Temp : {1}; PointX : {2}; PointY : {3}",
                                    stOutCome.regionId, 0.1 * stOutCome.minTmp, stOutComePoint1.x, stOutComePoint1.y);
                                }
                            }
                        }


                        // ch:释放图像 | en:Free image
                        device.StreamGrabber.FreeImageBuffer(frameOut);
                    }
                }
            }
            
        }

        private void bnStartGrab_Click(object sender, EventArgs e)
        {
            if (false == isOpen || true == isGrabbing || null == device)
            {
                return;
            }

            // ch:标志位置true | en:Set position bit true
            isGrabbing = true;

            m_hReceiveThread =  new Thread(ReceiveThreadProcess);
            m_hReceiveThread.Start();

            // ch:开始采集 | en:Start Grabbing
            int result = device.StreamGrabber.StartGrabbing();
            if (MvError.MV_OK != result)
            {
                isGrabbing = false;
                m_hReceiveThread.Join();
                ShowErrorMsg("Start Grabbing Fail!", result);
                return;
            }

            isGrabbing = true;
            EnableControls(true);
        }

        private void bnClose_Click(object sender, EventArgs e)
        {
            // ch:取流标志位清零 | en:Reset flow flag bit
            if (isGrabbing == true)
            {
                isGrabbing = false;
                m_hReceiveThread.Join();
            }

            // ch:关闭设备 | en:Close Device
            if (device != null)
            {
                device.Close();
                device.Dispose();
            }

            isGrabbing = false;
            isOpen = false;

            // ch:控件操作 | en:Control Operation
            EnableControls(true);
        }

        private void bnStopGrab_Click(object sender, EventArgs e)
        {
            if (false == isOpen || false == isGrabbing || null == device)
            {
                return;
            }

            // ch:标志位设为false | en:Set flag bit false
            isGrabbing = false;
            m_hReceiveThread.Join();

            // ch:停止采集 | en:Stop Grabbing
            int result = device.StreamGrabber.StopGrabbing();
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Stop Grabbing Fail!", result);
            }

            isGrabbing = false;
            // ch:控件操作 | en:Control Operation
            EnableControls(true);
        }

        private void cbPixelFormat_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (false == isGrabbing)
            {
                int result = device.Parameters.SetEnumValueByString("PixelFormat", cbPixelFormat.SelectedItem.ToString());
                if (result != MvError.MV_OK)
                {
                    ShowErrorMsg("Set PixelFormat Fail!", result);
                }
            }
        }

        private void cbDisplaySource_SelectedIndexChanged(object sender, EventArgs e)
        {
            int result = device.Parameters.SetEnumValueByString("OverScreenDisplayProcessor", cbDisplaySource.SelectedItem.ToString());
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set OverScreenDisplayProcessor Fail!", result);
            }
        }

        private void cbPaletteMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            int result = device.Parameters.SetEnumValueByString("PalettesMode", cbPaletteMode.SelectedItem.ToString());
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set PalettesMode Fail!", result);
            }
        }

        private void cbLegendCheck_CheckedChanged(object sender, EventArgs e)
        {
            bool bLegendCheck = cbLegendCheck.Checked;

            int result = device.Parameters.SetBoolValue("LegendDisplayEnable", bLegendCheck);
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set LegendDisplayEnable Fail!", result);
                return;
            }

            result = device.Parameters.SetCommandValue("TempControlLoad");
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Exec TempControlLoad Fail!", result);
            }
        }

        private void cbExportModeCheck_CheckedChanged(object sender, EventArgs e)
        {
            bool bExportModeCheck = cbExportModeCheck.Checked;

            int result = device.Parameters.SetBoolValue("MtExpertMode", bExportModeCheck);
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set ExpertMode Fail!", result);
                return;
            }

            result = device.Parameters.SetCommandValue("TempControlLoad");
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Exec TempControlLoad Fail!", result);
            }
        }

        private void cbRegionSelect_SelectedIndexChanged(object sender, EventArgs e)
        {
            int result = device.Parameters.SetEnumValueByString("TempRegionSelector", cbRegionSelect.SelectedItem.ToString());
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set TempRegionSelector Fail!", result);
            }
        }

        private void cbMeasureRange_SelectedIndexChanged(object sender, EventArgs e)
        {
            int result = device.Parameters.SetEnumValueByString("TempMeasurementRange", cbMeasureRange.SelectedItem.ToString());
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set TempMeasurementRange Fail!", result);
            }
        }

        private void bnSetParameter_Click(object sender, EventArgs e)
        {
            try
            {
                int.Parse(teTransmissivity.Text);
                float.Parse(teTargetDistance.Text);
                float.Parse(teEmissivity.Text);
            }
            catch
            {
                ShowErrorMsg("Please enter correct type!", 0);
                return;
            }

            int result = device.Parameters.SetIntValue("AtmosphericTransmissivity", long.Parse(teTransmissivity.Text));
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set AtmosphericTransmissivity Fail!", result);
            }

            result = device.Parameters.SetFloatValue("TargetDistance", float.Parse(teTargetDistance.Text));
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set TargetDistance Fail!", result);
            }

            result = device.Parameters.SetFloatValue("FullScreenEmissivity", float.Parse(teEmissivity.Text) + 0.000001F);
            if (result != MvError.MV_OK)
            {
                ShowErrorMsg("Set FullScreenEmissivity Fail!", result);
            }
        }

        private void bnRegionSetting_Click(object sender, EventArgs e)
        {
            if (cbRegionSelect.SelectedIndex < 0)
            {
                ShowErrorMsg("No Region is selected", MvError.MV_OK);
                return;
            }

            bool bExportModeCheck = cbExportModeCheck.Checked;
            InfraredDemo.RegionSettingForm = new FormRegionSetting(ref device, ref cbRegionSelect, ref bExportModeCheck);

            InfraredDemo.RegionSettingForm.Show();
            RegionSettingForm.Show();
        }

        private void bnWarningSetting_Click(object sender, EventArgs e)
        {
            if (cbRegionSelect.SelectedIndex < 0)
            {
                ShowErrorMsg("No Region is selected", MvError.MV_OK);
                return;
            }

            InfraredDemo.AlarmSettingForm = new FormAlarmSetting(ref device, ref cbRegionSelect);
            InfraredDemo.AlarmSettingForm.Show();
            AlarmSettingForm.Show();
        }

        private void InfraredDemo_Closing(object sender, FormClosingEventArgs e)
        {
            bnClose_Click(sender, null);

            // ch: 反初始化SDK | en: Finalize SDK
            SDKSystem.Finalize();
        }
 
    }
}
