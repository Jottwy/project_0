using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PolymindGames
{
    public class GroupDefinition<Group, Member> : DataDefinition<Group>
        where Group : GroupDefinition<Group, Member>
        where Member : GroupMemberDefinition<Member, Group>
    {
        [SerializeField, SpritePreview]
        private Sprite _icon;

        [SerializeField, Disable, SpaceArea]
        [ReorderableList(ListStyle.Lined, fixedSize: true, HasLabels = false)]
        private Member[] _members = Array.Empty<Member>();

        public Member[] Members => _members;
        public override Sprite Icon => _icon;
        
		#region Editor
#if UNITY_EDITOR
        public void SetMembers_EditorOnly(Member[] members)
        {
            members ??= Array.Empty<Member>();
            if (members != _members)
            {
                _members = members;
                ValidateAndFilterMembers();
                EditorUtility.SetDirty(this);
            }
        }
        
        public bool AddMember_EditorOnly(Member member)
        {
            if (!_members.Contains(member))
            {
                ArrayUtility.Add(ref _members, member);
                EditorUtility.SetDirty(this);
                ValidateAndFilterMembers();
                return true;
            }

            return false;
        }
        
        public bool RemoveMember_EditorOnly(Member member)
        {
            if (_members.Contains(member))
            {
                ArrayUtility.Remove(ref _members, member);
                EditorUtility.SetDirty(this);
                ValidateAndFilterMembers();
                return true;
            }

            return false;
        }

        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            base.Validate_EditorOnly(in validationContext);

            if (validationContext.IsFromToolsWindow)
            {
                if (validationContext.Trigger is ValidationTrigger.Refresh)
                {
                    ValidateAndFilterMembers();
                }
            }
            else
            {
                if (validationContext.Trigger is ValidationTrigger.Created or ValidationTrigger.Duplicated)
                {
                    _members = Array.Empty<Member>();
                }
            }
        }
        
        private void ValidateAndFilterMembers()
        {
            if (_members.Length == 0)
                return;
            
            for (int i = _members.Length - 1; i >= 0; i--)
            {
                var member = _members[i];
                if (member == null || member.ParentGroup != this)
                {
                    ArrayUtility.RemoveAt(ref _members, i);
                    EditorUtility.SetDirty(this);
                }
            }
        }
#endif
		#endregion
    }
}