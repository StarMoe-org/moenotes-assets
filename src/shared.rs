use anyhow::{Result, bail};
use std::{
    collections::HashMap,
    future::Future,
    sync::{
        Arc, Mutex, Weak,
        atomic::{AtomicUsize, Ordering},
    },
};
use tokio::sync::watch;
use tokio_util::sync::CancellationToken;

type Outcome<T> = Option<std::result::Result<Arc<T>, String>>;
pub struct Shared<T> {
    result: watch::Sender<Outcome<T>>,
    cancel: CancellationToken,
    users: AtomicUsize,
}
pub type Registry<T> = Mutex<HashMap<String, Weak<Shared<T>>>>;
struct Waiter<T>(Arc<Shared<T>>);
impl<T> Drop for Waiter<T> {
    fn drop(&mut self) {
        if self.0.users.fetch_sub(1, Ordering::AcqRel) == 1 {
            self.0.cancel.cancel();
        }
    }
}
pub struct Lease<T> {
    value: Arc<T>,
    _waiter: Waiter<T>,
}
impl<T> std::ops::Deref for Lease<T> {
    type Target = T;
    fn deref(&self) -> &T {
        &self.value
    }
}

pub async fn join<T, F, Fut>(
    registry: &Registry<T>,
    key: String,
    caller: &CancellationToken,
    create: F,
) -> Result<Lease<T>>
where
    T: Send + Sync + 'static,
    F: FnOnce(CancellationToken) -> Fut + Send + 'static,
    Fut: Future<Output = Result<T>> + Send + 'static,
{
    let waiter = {
        let mut map = registry.lock().unwrap();
        map.retain(|_, v| v.strong_count() > 0);
        let existing = map
            .get(&key)
            .and_then(Weak::upgrade)
            .filter(|s| !s.cancel.is_cancelled());
        let existing = existing.filter(|s| {
            s.users
                .fetch_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                    if n > 0 { n.checked_add(1) } else { None }
                })
                .is_ok()
        });
        if let Some(s) = existing {
            Waiter(s)
        } else {
            let (tx, _) = watch::channel(None);
            let s = Arc::new(Shared {
                result: tx,
                cancel: CancellationToken::new(),
                users: AtomicUsize::new(1),
            });
            map.insert(key, Arc::downgrade(&s));
            let producer = s.clone();
            tokio::spawn(async move {
                let r = match tokio::spawn(create(producer.cancel.clone())).await {
                    Ok(result) => result.map(Arc::new).map_err(|e| format!("{e:#}")),
                    Err(_) => Err("shared operation terminated unexpectedly".into()),
                };
                producer.result.send_replace(Some(r));
            });
            Waiter(s)
        }
    };
    let mut rx = waiter.0.result.subscribe();
    loop {
        if caller.is_cancelled() {
            bail!("cancelled")
        }
        let ready = rx.borrow().clone();
        if let Some(r) = ready {
            return r
                .map(|value| Lease {
                    value,
                    _waiter: waiter,
                })
                .map_err(anyhow::Error::msg);
        }
        tokio::select! {_ = caller.cancelled()=>bail!("cancelled"),r=rx.changed()=>{r?;}}
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn completed_payload_stays_shared_until_last_lease() {
        let map = Registry::default();
        let token = CancellationToken::new();
        let first = join(&map, "a".into(), &token, |_| async { Ok(9) })
            .await
            .unwrap();
        let second = join(&map, "a".into(), &token, |_| async {
            anyhow::bail!("should reuse")
        })
        .await
        .unwrap();
        assert_eq!(*second, 9);
        drop(first);
        drop(second);
        let third = join(&map, "a".into(), &token, |_| async { Ok(10) })
            .await
            .unwrap();
        assert_eq!(*third, 10);
    }
    #[tokio::test]
    async fn producer_panic_does_not_hang() {
        let map = Registry::<u32>::default();
        let result = join(&map, "panic".into(), &CancellationToken::new(), |_| async {
            panic!("synthetic");
            #[allow(unreachable_code)]
            Ok(0)
        })
        .await;
        assert!(result.is_err());
    }
    #[tokio::test]
    async fn one_cancel_keeps_other_waiter() {
        let map = Arc::new(Registry::default());
        let a = CancellationToken::new();
        let b = CancellationToken::new();
        let started = Arc::new(tokio::sync::Notify::new());
        let task = {
            let map = map.clone();
            let a = a.clone();
            let n = started.clone();
            tokio::spawn(async move {
                join(&map, "x".into(), &a, move |token| async move {
                    n.notify_one();
                    tokio::time::sleep(std::time::Duration::from_millis(100)).await;
                    assert!(!token.is_cancelled());
                    Ok(7u32)
                })
                .await
            })
        };
        started.notified().await;
        let second = {
            let map = map.clone();
            tokio::spawn(async move {
                join(&map, "x".into(), &b, |_| async {
                    panic!("duplicate producer");
                    #[allow(unreachable_code)]
                    Ok(0u32)
                })
                .await
            })
        };
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        a.cancel();
        assert!(task.await.unwrap().is_err());
        assert_eq!(*second.await.unwrap().unwrap(), 7);
    }
}
