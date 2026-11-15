using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes Genre object.
    /// </summary>
    public class Genre : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="Genre"/>.
        /// </summary>
        public Genre(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Genre"/>.
        /// </summary>
        /// <param name="name"></param>
        public Genre(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        /// Gets empty genre.
        /// </summary>
        public static readonly Genre Empty = new Genre(Guid.Empty, string.Empty);
    }
}
