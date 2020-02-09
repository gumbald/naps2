﻿using System;

namespace NAPS2.Config
{
    public class ChildConfigProvider<TParent, TChild> : ConfigProvider<TChild>
    {
        private readonly ConfigProvider<TParent> parentProvider;
        private readonly Func<TParent, TChild> childSelector;

        public ChildConfigProvider(ConfigProvider<TParent> parentProvider, Func<TParent, TChild> childSelector)
        {
            this.parentProvider = parentProvider;
            this.childSelector = childSelector;
        }

        protected override T GetInternal<T>(Func<TChild, T> func) => parentProvider.Get(parent =>
        {
            var child = childSelector(parent) ?? throw new InvalidOperationException("Child config should not be null");
            return func(child);
        });
    }
}
