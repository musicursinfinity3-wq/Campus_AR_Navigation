using UnityEngine;

namespace CampusNavigation.UI
{
    public class ScanningLineAnimation : MonoBehaviour
    {
        [SerializeField] private float speed = 200f;
        [SerializeField] private float height = 300f;

        private RectTransform rectTransform;
        private float startY;

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            startY = rectTransform.anchoredPosition.y;
        }

        private void Update()
        {
            float newY = Mathf.PingPong(Time.time * speed, height) - (height / 2f);
            rectTransform.anchoredPosition = new Vector3(rectTransform.anchoredPosition.x, newY, 0f);
        }
    }
}