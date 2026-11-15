using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes Tag object.
    /// </summary>
    public class Tag : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="Tag"/>.
        /// </summary>
        public Tag(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Tag"/>.
        /// </summary>
        /// <param name="name"></param>
        public Tag(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty tag.
        /// </summary>
        public static readonly Tag Empty = new Tag(Guid.Empty, string.Empty);
    }
}
