using System.Collections;
using System.Collections.Generic;
using Unity.XR.PXR;
using UnityEngine;

public class MotionTrackingDebug : MonoBehaviour
{
    TrackingStateCode trackingState;
    EyeTrackingStartInfo startInfo;
    EyeTrackingMode eyeTrackingMode;
    EyeTrackingDataGetInfo info;

    [HideInInspector]
    public EyeTrackingData eyeTrackingData;


    [SerializeField]
    private TMPro.TextMeshProUGUI leftEyePose;
    [SerializeField]
    private TMPro.TextMeshProUGUI rightEyePose;
    [SerializeField]
    private TMPro.TextMeshProUGUI combinedEyePose;

    [SerializeField]
    private TMPro.TextMeshProUGUI leftEyeOpenness;
    [SerializeField]
    private TMPro.TextMeshProUGUI rightEyeOpenness;

    [SerializeField]
    private TMPro.TextMeshProUGUI leftEyePupil;
    [SerializeField]
    private TMPro.TextMeshProUGUI rightEyePupil;

    [SerializeField]
    private TMPro.TextMeshProUGUI leftEyeBlink;
    [SerializeField]
    private TMPro.TextMeshProUGUI rightEyeBlink;


    long timestamp;

    //Eye data/pose
    public Posef leftEyePoseData;
    public Posef rightEyePoseData;

    //Pupil info
    public EyePupilInfo pupilInfo;

    //Blink info
    public bool isLeftBlink;
    public bool isRightBlink;

    public void Start()
    {     
        // Request the eye tracking service for the current app
        trackingState = (TrackingStateCode)PXR_MotionTracking.WantEyeTrackingService();

        eyeTrackingMode = EyeTrackingMode.PXR_ETM_BOTH;

        startInfo = new EyeTrackingStartInfo();

        startInfo.needCalibration = 1;
        startInfo.mode = eyeTrackingMode;
        trackingState = (TrackingStateCode)PXR_MotionTracking.StartEyeTracking(ref startInfo);

        // Get eye tracking data
        info = new EyeTrackingDataGetInfo
        {
            displayTime = 0,
            flags = EyeTrackingDataGetFlags.PXR_EYE_DEFAULT
                    | EyeTrackingDataGetFlags.PXR_EYE_POSITION
                    | EyeTrackingDataGetFlags.PXR_EYE_ORIENTATION
        };

        eyeTrackingData = new EyeTrackingData();
        trackingState = (TrackingStateCode)PXR_MotionTracking.GetEyeTrackingData(ref info, ref eyeTrackingData);
        Debug.Log("Tracking state: " + trackingState);
    }

    private void Update()
    {
        UpdatePupilData();   
    }

    unsafe void UpdatePupilData()
    {
        PXR_MotionTracking.GetEyeTrackingData(ref info, ref eyeTrackingData);

        //This interface only supports combined eye pose data
        combinedEyePose.text = eyeTrackingData.eyeDatas[2].pose.ToString();

        //So we use GetPerEyePose
        PXR_MotionTracking.GetPerEyePose(ref timestamp, ref leftEyePoseData, ref rightEyePoseData);

        leftEyePose.text = leftEyePoseData.ToString();
        rightEyePose.text = rightEyePoseData.ToString();

        leftEyeOpenness.text = eyeTrackingData.eyeDatas[0].openness.ToString("F2");
        rightEyeOpenness.text = eyeTrackingData.eyeDatas[1].openness.ToString("F2");

        pupilInfo = new();

        PXR_MotionTracking.GetEyePupilInfo(ref pupilInfo);

        float* leftEyePupilPosition;
        float* rightEyePupilPosition;

        // Pinning the left eye pupil position buffer
        fixed (float* leftEyePupilPosPtr = pupilInfo.leftEyePupilPosition)
        {
            leftEyePupilPosition = leftEyePupilPosPtr;

            // Accessing and displaying left eye pupil information
            leftEyePupil.text = "Diameter: " + pupilInfo.leftEyePupilDiameter.ToString("F2") + "\n Position: " + *leftEyePupilPosition;
        }

        // Pinning the right eye pupil position buffer
        fixed (float* rightEyePupilPosPtr = pupilInfo.rightEyePupilPosition)
        {
            rightEyePupilPosition = rightEyePupilPosPtr;

            // Accessing and displaying right eye pupil information
            rightEyePupil.text = "Diameter: " + pupilInfo.rightEyePupilDiameter.ToString("F2") + "\n Position: " + *rightEyePupilPosition;
        }


        PXR_MotionTracking.GetEyeBlink(ref timestamp, ref isLeftBlink, ref isRightBlink);

        leftEyeBlink.text = isLeftBlink.ToString();
        rightEyeBlink.text = isRightBlink.ToString();
    }

}
