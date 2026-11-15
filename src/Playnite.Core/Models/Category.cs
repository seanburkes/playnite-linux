using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes category object.
    /// </summary>
    public class Category : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="Category"/>.
        /// </summary>
        public Category(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Category"/>.
        /// </summary>
        /// <param name="name">Category name.</param>
        public Category(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty category.
        /// </summary>
        public static readonly Category Empty = new Category(Guid.Empty, string.Empty);
    }
}
