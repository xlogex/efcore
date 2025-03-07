﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Microsoft.EntityFrameworkCore;

public class BindingInterceptionSqliteTest : BindingInterceptionTestBase,
    IClassFixture<BindingInterceptionSqliteTest.BindingInterceptionSqliteFixture>
{
    public BindingInterceptionSqliteTest(BindingInterceptionSqliteFixture fixture)
        : base(fixture)
    {
    }

    public class BindingInterceptionSqliteFixture : SingletonInterceptorsFixtureBase
    {
        protected override string StoreName
            => "BindingInterception";

        protected override ITestStoreFactory TestStoreFactory
            => SqliteTestStoreFactory.Instance;

        protected override IServiceCollection InjectInterceptors(
            IServiceCollection serviceCollection,
            IEnumerable<ISingletonInterceptor> injectedInterceptors)
            => base.InjectInterceptors(serviceCollection.AddEntityFrameworkSqlite(), injectedInterceptors);
    }
}
