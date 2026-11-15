using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace Playnite.Core.Models
{
    /// <summary>
    /// Describes Company object.
    /// </summary>
    public class Company : DatabaseObject
    {
        /// <summary>
        /// Creates new instance of <see cref="Company"/>.
        /// </summary>
        public Company(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Company"/>.
        /// </summary>
        /// <param name="name"></param>
        public Company(Guid id, string name) : base(id)
        {
            Name = name;
        }

        /// <summary>
        ///
        /// </summary>
        public static readonly Company Empty = new Company(Guid.Empty, string.Empty);
    }

    /// <summary>
    /// Describes Developer object.
    /// </summary>
    public class Developer : Company
    {
        /// <summary>
        /// Creates new instance of <see cref="Developer"/>.
        /// </summary>
        public Developer(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Developer"/>.
        /// </summary>
        /// <param name="name"></param>
        public Developer(Guid id, string name) : base(id, name)
        {
        }
    }

    /// <summary>
    /// Describes Publisher object.
    /// </summary>
    public class Publisher : Company
    {
        /// <summary>
        /// Creates new instance of <see cref="Publisher"/>.
        /// </summary>
        public Publisher(Guid id) : base(id)
        {
        }

        /// <summary>
        /// Creates new instance of <see cref="Publisher"/>.
        /// </summary>
        /// <param name="name"></param>
        public Publisher(Guid id, string name) : base(id, name)
        {
        }
    }
}
