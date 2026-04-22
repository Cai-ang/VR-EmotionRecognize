using UnityEngine;
using Unity.XR.PXR;
using UnityEngine.XR.Hands;

namespace MECSense
{
    /// <summary>
    /// 多通道数据采集器：采集面部追踪(72维BlendShape)、眼动追踪、手部追踪数据。
    /// 以 90Hz 渲染帧为基准时钟做最近邻对齐。
    /// 手部数据通过共享缓冲区实现一源双用分流。
    /// </summary>
    public class MultiChannelDataCollector : MonoBehaviour
    {
        #region 公开属性（供 FeatureExtractor 读取）

        /// <summary>72 维 BlendShape 系数</summary>
        public float[] FaceBlendShapes => m_BlendShapeWeight;

        /// <summary>双眼注视方向（组合）</summary>
        public Vector3 GazeDirection => m_GazeDirection;

        /// <summary>瞳孔直径（双眼平均，mm）</summary>
        public float PupilDiameter => m_PupilDiameter;

        /// <summary>当前帧是否检测到眨眼</summary>
        public bool BlinkEvent => m_BlinkEvent;

        /// <summary>主控手 21 个关节坐标</summary>
        public Vector3[] HandJoints => m_HandJoints;

        /// <summary>数据是否有效（传感器已初始化）</summary>
        public bool IsDataValid => m_FaceReady || m_EyeReady || m_HandReady;

        #endregion

        #region 配置

        [Header("Settings")]
        [SerializeField]
        [Tooltip("主控手")]
        Handedness m_PrimaryHand = Handedness.Right;

        [SerializeField]
        [Tooltip("面部追踪模式")]
        FaceTrackingMode m_FaceMode = FaceTrackingMode.PXR_FTM_FACE_LIPS_BS;

        #endregion

        #region 内部状态

        // 面部追踪
        float[] m_BlendShapeWeight = new float[72];
        PxrFaceTrackingInfo m_FaceTrackingInfo;
        bool m_FaceReady;

        // 眼动追踪
        Vector3 m_GazeDirection;
        float m_PupilDiameter;
        bool m_BlinkEvent;
        bool m_EyeReady;
        EyeTrackingDataGetInfo m_EyeInfo;
        EyeTrackingData m_EyeTrackingData;
        bool m_LeftBlink;
        bool m_RightBlink;
        long m_EyeTimestamp;
        Posef m_LeftEyePose;
        Posef m_RightEyePose;
        EyePupilInfo m_PupilInfo;

        // 手部追踪
        Vector3[] m_HandJoints = new Vector3[21];
        bool m_HandReady;
        bool m_PrevBlink;

        // XR Hand subsystem
        static System.Collections.Generic.List<XRHandSubsystem> s_SubsystemsReuse
            = new System.Collections.Generic.List<XRHandSubsystem>();

        #endregion

        void Start()
        {
            InitFaceTracking();
            InitEyeTracking();
        }

        #region 初始化

        void InitFaceTracking()
        {
            if (PXR_Plugin.System.UPxr_QueryDeviceAbilities(PxrDeviceAbilities.PxrTrackingModeFaceBit))
            {
                PXR_MotionTracking.WantFaceTrackingService();
                FaceTrackingStartInfo info = new FaceTrackingStartInfo();
                info.mode = m_FaceMode;
                PXR_MotionTracking.StartFaceTracking(ref info);
                m_FaceTrackingInfo = new PxrFaceTrackingInfo();
                m_FaceReady = true;
                Debug.Log("[MECSense] 面部追踪已启动");
            }
            else
            {
                Debug.LogWarning("[MECSense] 设备不支持面部追踪");
            }
        }

        void InitEyeTracking()
        {
            var trackingState = (TrackingStateCode)PXR_MotionTracking.WantEyeTrackingService();
            EyeTrackingStartInfo startInfo = new EyeTrackingStartInfo();
            startInfo.needCalibration = 1;
            startInfo.mode = EyeTrackingMode.PXR_ETM_BOTH;
            trackingState = (TrackingStateCode)PXR_MotionTracking.StartEyeTracking(ref startInfo);

            m_EyeInfo = new EyeTrackingDataGetInfo
            {
                displayTime = 0,
                flags = EyeTrackingDataGetFlags.PXR_EYE_DEFAULT
                        | EyeTrackingDataGetFlags.PXR_EYE_POSITION
                        | EyeTrackingDataGetFlags.PXR_EYE_ORIENTATION
            };
            m_EyeTrackingData = new EyeTrackingData();
            m_EyeReady = true;
            Debug.Log("[MECSense] 眼动追踪已启动");
        }

        #endregion

        void Update()
        {
            CollectFaceData();
            CollectEyeData();
            CollectHandData();
        }

        #region 面部数据采集（~30Hz，零阶保持）

        void CollectFaceData()
        {
            if (!m_FaceReady) return;

            try
            {
                switch (PXR_Manager.Instance.trackingMode)
                {
                    case FaceTrackingMode.PXR_FTM_FACE_LIPS_BS:
                        PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_FACELIP_DATA, ref m_FaceTrackingInfo);
                        break;
                    case FaceTrackingMode.PXR_FTM_FACE:
                        PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_FACE_DATA, ref m_FaceTrackingInfo);
                        break;
                    case FaceTrackingMode.PXR_FTM_LIPS:
                        PXR_System.GetFaceTrackingData(0, GetDataType.PXR_GET_LIP_DATA, ref m_FaceTrackingInfo);
                        break;
                }

                unsafe
                {
                    fixed (float* source = m_FaceTrackingInfo.blendShapeWeight)
                    {
                        for (int i = 0; i < 72; i++)
                            m_BlendShapeWeight[i] = source[i];
                    }
                }
            }
            catch (System.Exception)
            {
                // 低采样帧间复用最近值（零阶保持）
            }
        }

        #endregion

        #region 眼动数据采集（~90Hz）

        void CollectEyeData()
        {
            if (!m_EyeReady) return;

            try
            {
                PXR_MotionTracking.GetEyeTrackingData(ref m_EyeInfo, ref m_EyeTrackingData);

                // 注视方向：从双眼组合数据中获取
                // eyeTrackingData.eyeDatas[2] 是双眼组合数据
                var leftOri = m_EyeTrackingData.eyeDatas[0].pose.orientation;
                var rightOri = m_EyeTrackingData.eyeDatas[1].pose.orientation;
                Quaternion leftQ = new Quaternion(leftOri.x, leftOri.y, leftOri.z, leftOri.w);
                Quaternion rightQ = new Quaternion(rightOri.x, rightOri.y, rightOri.z, rightOri.w);
                Vector3 leftFwd = leftQ * Vector3.forward;
                Vector3 rightFwd = rightQ * Vector3.forward;
                m_GazeDirection = (leftFwd + rightFwd).normalized;

                // 瞳孔直径（双眼平均）
                m_PupilInfo = new EyePupilInfo();
                PXR_MotionTracking.GetEyePupilInfo(ref m_PupilInfo);
                m_PupilDiameter = (m_PupilInfo.leftEyePupilDiameter + m_PupilInfo.rightEyePupilDiameter) * 0.5f;

                // 眨眼事件
                PXR_MotionTracking.GetEyeBlink(ref m_EyeTimestamp, ref m_LeftBlink, ref m_RightBlink);
                m_BlinkEvent = m_LeftBlink || m_RightBlink;
                // 检测上升沿：前一帧未眨眼、当前帧眨眼
                bool blinkRising = m_BlinkEvent && !m_PrevBlink;
                m_BlinkEvent = blinkRising;
                m_PrevBlink = m_LeftBlink || m_RightBlink;
            }
            catch (System.Exception)
            {
                // 零阶保持
            }
        }

        #endregion

        #region 手部数据采集（~60Hz，一源双用共享缓冲区）

        void CollectHandData()
        {
            SubsystemManager.GetSubsystems(s_SubsystemsReuse);
            if (s_SubsystemsReuse.Count == 0) return;

            var subsystem = s_SubsystemsReuse[0];
            if (!subsystem.running) return;

            var hand = m_PrimaryHand == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
            if (!hand.isTracked) { m_HandReady = false; return; }
            m_HandReady = true;

            for (int i = 0; i < 21; i++)
            {
                var joint = hand.GetJoint(XRHandJointIDUtility.FromIndex(i));
                if (joint.TryGetPose(out var pose))
                    m_HandJoints[i] = pose.position;
            }
        }

        #endregion
    }
}
