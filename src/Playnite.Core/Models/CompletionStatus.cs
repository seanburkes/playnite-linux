using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Represents game completion status.
    /// </summary>
    public class CompletionStatus : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="CompletionStatus"/>.
        /// </summary>
        public CompletionStatus(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="CompletionStatus"/>.
        /// </summary>
        /// <param name="name"></param>
        public CompletionStatus(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty CompletionStatus.
        /// </summary>
        public static readonly CompletionStatus Empty = new CompletionStatus(Guid.Empty, string.Empty);
    }
}
