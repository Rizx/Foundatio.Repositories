﻿using System;
using Nest;

namespace Foundatio.Repositories.Elasticsearch.Queries.Builders {
    public class ElasticFilterQueryBuilder : ElasticQueryBuilderBase {
        public override void BuildFilter<T>(object query, object options, ref FilterContainer container) {
            var elasticQuery = query as IElasticFilterQuery;
            if (elasticQuery?.ElasticFilter == null)
                return;

            container &= elasticQuery.ElasticFilter;
        }
    }
}