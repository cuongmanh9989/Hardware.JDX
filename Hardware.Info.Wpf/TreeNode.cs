using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Một nút trong cây phần cứng: tiêu đề, giá trị (nếu là nút lá) và các nút con.
    /// </summary>
    public class TreeNode : INotifyPropertyChanged
    {
        private bool _isExpanded;

        /// <summary>Biểu tượng hiển thị trước tiêu đề (chỉ dùng cho nhóm cấp 1).</summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>Tên thuộc tính hoặc tên nhóm.</summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>Giá trị của thuộc tính; rỗng nếu là nút nhóm.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>Đường dẫn đầy đủ từ gốc, dùng để khôi phục trạng thái mở/đóng sau khi làm mới.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Danh sách nút con.</summary>
        public List<TreeNode> Children { get; } = new List<TreeNode>();

        /// <summary>Nút đang được mở rộng hay không (liên kết hai chiều với TreeViewItem).</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Mở hoặc đóng nút này và toàn bộ nút con.</summary>
        public void SetExpandedRecursive(bool expanded)
        {
            IsExpanded = expanded;
            foreach (TreeNode child in Children)
                child.SetExpandedRecursive(expanded);
        }

        /// <summary>Ghi cây ra văn bản thụt lề (dùng cho chức năng sao chép).</summary>
        public void AppendText(StringBuilder sb, int level)
        {
            sb.Append(' ', level * 2).Append(Header);
            if (Value.Length > 0)
                sb.Append(": ").Append(Value);
            sb.AppendLine();

            foreach (TreeNode child in Children)
                child.AppendText(sb, level + 1);
        }
    }
}
