﻿using System;
using Foundatio.Repositories.Elasticsearch.Configuration;

namespace Foundatio.Repositories.Elasticsearch.Tests.Configuration {
    public sealed class IdentityIndex : Index {
        public IdentityIndex(IElasticConfiguration elasticConfiguration) : base(elasticConfiguration, "identity") {
            AddType(Identity = new IdentityType(this));
        }

        public IdentityType Identity { get; }
    }
}