using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Linq;

namespace UnityEngine.Experimental.PostProcessing
{
    [Serializable]
    public class PostProcessEffectSettings : ScriptableObject
    {
        // Used to control the state of this override - handy to quickly turn a volume override
        // on & off in the editor
        public bool active = true;

        public BoolParameter enabled = new BoolParameter { value = false };

        internal ReadOnlyCollection<ParameterOverride> parameters;

        void OnEnable()
        {
            // Automatically grab all fields of type ParameterOverride for this instance
            parameters = GetType()
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(t => t.FieldType.IsSubclassOf(typeof(ParameterOverride)))
                .Select(t => (ParameterOverride)t.GetValue(this))
                .ToList()
                .AsReadOnly();
        }

        public void SetAllOverridesTo(bool state, bool excludeEnabled = true)
        {
            foreach (var prop in parameters)
            {
                if (excludeEnabled && prop == enabled)
                    continue;

                prop.overrideState = state;
            }
        }

        public virtual bool IsEnabledAndSupported()
        {
            return enabled.value;
        }

        // Custom hashing function used to compare the state of settings (it's not meant to be
        // unique but to be a quick way to check if two setting sets have the same state or not).
        // Hash collision rate should be pretty low.
        public int GetHash()
        {
            unchecked
            {
                //return parameters.Aggregate(17, (i, p) => i * 23 + p.GetHash());

                int hash = 17;

                foreach (var p in parameters)
                    hash = hash * 23 + p.GetHash();

                return hash;
            }
        }
    }
}
