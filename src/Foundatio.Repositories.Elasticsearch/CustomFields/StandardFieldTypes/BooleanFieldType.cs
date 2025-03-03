﻿using System.Threading.Tasks;
using Nest;

namespace Foundatio.Repositories.Elasticsearch.CustomFields;

public class BooleanFieldType : ICustomFieldType {
    public static string IndexType = "bool";
    public string Type => "bool";

    public Task<object> TransformToIdxAsync(object value) {
        return Task.FromResult(value);
    }

    public virtual IProperty ConfigureMapping<T>(SingleMappingSelector<T> map) where T : class {
        return map.Boolean(mp => mp);
    }
}