using UnityEngine;
using UnityEngine.EventSystems;

namespace FreeWorldMultiplayer
{
    // Only the MOD entry uses this component. The native Button keeps its own click handler.
    internal sealed class MultiplayerEntryDrag : MonoBehaviour, IInitializePotentialDragHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        // Runtime-only position survives replacement of the game's Canvas/entry object.
        private static bool hasSavedPosition;
        private static Vector2 savedPosition;
        private readonly Vector3[] corners = new Vector3[4];
        private UnityEngine.UI.Button button;
        private RectTransform rect;
        private RectTransform dragParent;
        private Vector2 pointerOffset;
        private int pointerId;
        private bool dragging;
        private bool initialized;
        private bool applyingLayout;

        internal static void Attach(UnityEngine.UI.Button entry)
        {
            var drag = entry.gameObject.AddComponent<MultiplayerEntryDrag>();
            drag.button = entry;
            drag.rect = (RectTransform)entry.transform;
            drag.initialized = true;
            drag.ApplyLayout();
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                eventData.useDragThreshold = true;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !isActiveAndEnabled
                || button == null || !button.IsActive() || !button.IsInteractable()) return;
            // The EventSystem has already applied its pixel drag threshold. Suppress only
            // this press, including when the pointer is released over the moved button.
            eventData.eligibleForClick = false;
            if (dragging) return;
            RectTransform parent = rect.parent as RectTransform;
            Vector2 press;
            if (parent == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, eventData.pressPosition, eventData.pressEventCamera, out press)) return;
            dragParent = parent;
            pointerOffset = press - (Vector2)rect.localPosition;
            pointerId = eventData.pointerId;
            dragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!dragging || eventData.pointerId != pointerId
                || eventData.button != PointerEventData.InputButton.Left) return;
            eventData.eligibleForClick = false;
            if (dragParent == null || rect.parent != dragParent) { ResetDrag(); return; }
            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                dragParent, eventData.position, eventData.pressEventCamera, out pointer)) return;
            Vector2 minimum, maximum;
            if (!TryGetLimits(out minimum, out maximum)) return;
            SetPosition(Clamp(pointer - pointerOffset, minimum, maximum));
            Vector2 position = rect.localPosition;
            // A temporarily smaller-than-button Canvas must not erase the saved axis.
            if (maximum.x > minimum.x) savedPosition.x = Mathf.InverseLerp(minimum.x, maximum.x, position.x);
            else if (!hasSavedPosition) savedPosition.x = 1;
            if (maximum.y > minimum.y) savedPosition.y = Mathf.InverseLerp(minimum.y, maximum.y, position.y);
            else if (!hasSavedPosition) savedPosition.y = 1;
            hasSavedPosition = true;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!dragging || eventData.pointerId != pointerId
                || eventData.button != PointerEventData.InputButton.Left) return;
            eventData.eligibleForClick = false;
            ResetDrag();
        }

        private void OnEnable() { ApplyLayout(); }
        private void OnDisable() { ResetDrag(); }
        private void OnTransformParentChanged() { ResetDrag(); ApplyLayout(); }
        private void OnRectTransformDimensionsChange() { ApplyLayout(); }

        private void ResetDrag() { dragging = false; dragParent = null; }

        private void ApplyLayout()
        {
            if (!initialized || applyingLayout || rect == null) return;
            applyingLayout = true;
            try
            {
                Vector2 minimum, maximum;
                if (!TryGetLimits(out minimum, out maximum)) return;
                Vector2 position = rect.localPosition;
                if (hasSavedPosition)
                    position = new Vector2(Mathf.Lerp(minimum.x, maximum.x, savedPosition.x),
                        Mathf.Lerp(minimum.y, maximum.y, savedPosition.y));
                SetPosition(Clamp(position, minimum, maximum));
            }
            finally { applyingLayout = false; }
        }

        private bool TryGetLimits(out Vector2 minimum, out Vector2 maximum)
        {
            minimum = maximum = Vector2.zero;
            RectTransform parent = rect.parent as RectTransform;
            if (parent == null || parent.rect.width <= 0 || parent.rect.height <= 0
                || rect.rect.width <= 0 || rect.rect.height <= 0) return false;
            // Parent-local coordinates account for CanvasScaler and camera-space UI;
            // transformed corners also preserve the entire button at the screen edges.
            rect.GetWorldCorners(corners);
            Vector2 first = (Vector2)parent.InverseTransformPoint(corners[0]) - (Vector2)rect.localPosition;
            Vector2 extentMin = first, extentMax = first;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector2 corner = (Vector2)parent.InverseTransformPoint(corners[i]) - (Vector2)rect.localPosition;
                extentMin = Vector2.Min(extentMin, corner);
                extentMax = Vector2.Max(extentMax, corner);
            }
            minimum = parent.rect.min - extentMin;
            maximum = parent.rect.max - extentMax;
            return true;
        }

        private static Vector2 Clamp(Vector2 position, Vector2 minimum, Vector2 maximum)
        {
            return new Vector2(minimum.x <= maximum.x ? Mathf.Clamp(position.x, minimum.x, maximum.x) : (minimum.x + maximum.x) * 0.5f,
                minimum.y <= maximum.y ? Mathf.Clamp(position.y, minimum.y, maximum.y) : (minimum.y + maximum.y) * 0.5f);
        }

        private void SetPosition(Vector2 position)
        {
            Vector3 local = rect.localPosition;
            if (local.x != position.x || local.y != position.y)
                rect.localPosition = new Vector3(position.x, position.y, local.z);
        }
    }
}
