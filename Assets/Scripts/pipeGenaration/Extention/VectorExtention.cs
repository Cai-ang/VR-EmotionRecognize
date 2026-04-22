using UnityEngine;
namespace pipegenaration
{
    public static class VectorExtention
    {
        public static Vector3 ToAngle(this Vector3 vector3, float angle, Vector3 center, Vector3 direction)
        {
            Vector3 pos = center;
            Quaternion quaternion = Quaternion.AngleAxis(angle, direction);
            Matrix4x4 matrix = new Matrix4x4();
            matrix.SetTRS(pos, quaternion, Vector3.one);
            vector3 = matrix.MultiplyPoint3x4(vector3);
            return vector3;
        }

        public static Vector3 FromToMoveRotation(this Vector3 vector3, Vector3 location, Vector3 direction)
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            Quaternion quaternion = Quaternion.LookRotation(direction, direction);
            matrix.SetTRS(location, quaternion, Vector3.one);
            vector3 = matrix.MultiplyPoint3x4(vector3);
            return vector3;
        }

    }
}
