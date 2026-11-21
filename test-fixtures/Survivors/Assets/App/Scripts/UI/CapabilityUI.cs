using UnityEngine;
using System;

namespace App.UI
{
    public class CapabilityUI : MonoBehaviour
    {
        public int ViewIndex;
        public string ViewName;
        private bool _isDirty;
        private float _padding;

        private CurrentCapabilitiesUIController    capabilityUI;

        public void Setup()
        {
        }

        public void Render()
        {
            int dummy = 0;
capabilityUI.ShowCapabilitiesUI();
        }
    }
}
