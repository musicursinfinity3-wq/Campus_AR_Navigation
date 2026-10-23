using UnityEngine;

namespace CampusNavigation.Visualizer
{
    public class FloatingDestinationPin : MonoBehaviour
    {
        [Header("Rotation Settings")]
        [Tooltip("Degrees per second around the vertical axis.")]
        [SerializeField] private float rotationSpeed = 60f;

        [Header("Bobbing Settings")]
        [Tooltip("Total height distance the pin moves up and down (in meters).")]
        [SerializeField] private float bobAmplitude = 0.15f;

        [Tooltip("Speed of the bobbing cycle.")]
        [SerializeField] private float bobFrequency = 2f;

        private Vector3 startLocalPos;

        private void Start()
        {
            startLocalPos = transform.localPosition;
        }

        private void Update()
        {
            // 1. Smooth spin around the world vertical axis (keeps it upright regardless of root tilt)
            transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.World);

            // 2. Smooth sine-wave bobbing up and down
            float yOffset = Mathf.Sin(Time.time * bobFrequency) * bobAmplitude;
            transform.localPosition = startLocalPos + new Vector3(0f, yOffset, 0f);
        }
    }
}