using UnityEngine;
using System;

namespace PolymindGames
{
    public abstract class GroupMemberDefinition<Member, Group> : DataDefinition<Member>
        where Member : GroupMemberDefinition<Member, Group>
        where Group : GroupDefinition<Group, Member>
    {
        [SerializeField, Disable]
        private Group _parentGroup;
        
        [NonSerialized]
        private string _cachedFullName;

        private const string UnassignedGroup = "No Group";

        public Group ParentGroup => _parentGroup;
        public bool HasParentGroup => _parentGroup != null;

        public override string FullName
        {
            get
            {
                if (string.IsNullOrEmpty(_cachedFullName))
                    ResetFullNameString();

                return _cachedFullName;
            }
        }

        private void ResetFullNameString()
        {
            string categoryName = ParentGroup != null ? ParentGroup.Name : UnassignedGroup;
            _cachedFullName = $"{categoryName} / {Name}";
        }

        #region Editor
#if UNITY_EDITOR
        public void SetParentGroup_EditorOnly(Group group)
        {
            if (_parentGroup != null)
                _parentGroup.RemoveMember_EditorOnly((Member)this);
            
            _parentGroup = group;
            
            if (_parentGroup != null)
                _parentGroup.AddMember_EditorOnly((Member)this);

            ResetFullNameString();
        }

        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            base.Validate_EditorOnly(in validationContext);

            if (!validationContext.IsFromToolsWindow)
            {
                switch (validationContext.Trigger)
                {
                    case ValidationTrigger.Created:
                        {
                            if (DataDefinition<Group>.Definitions.Length > 0)
                                _parentGroup = DataDefinition<Group>.Definitions[0];
                            break;
                        }
                    case ValidationTrigger.Duplicated:
                        {
                            if (_parentGroup != null)
                                _parentGroup.AddMember_EditorOnly((Member)this);
                            break;
                        }
                    default:
                        return;
                }
            }
            else
            {
                if (_parentGroup != null)
                    _parentGroup.AddMember_EditorOnly((Member)this);
            }
        }
#endif
        #endregion
    }
}