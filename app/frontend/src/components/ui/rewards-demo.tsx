import { useState } from "react";
import { Flame, Gift } from "lucide-react";
import { Button } from "./button";

export type RewardsDemoAction = "status" | "check_in" | "redeem" | "unredeem" | "complete" | "reset";

export interface RewardsDemoSnapshot {
    checkedIn: boolean;
    balance: number;
    rewardItem: string;
    rewardCost: number;
    pointsPerDollar: number;
    demoCodes: string[];
    redeemed: boolean;
    completed: boolean;
    canRedeem: boolean;
    pointsToEarn: number;
    pointsAwarded: number;
    discount: number;
    message: string;
}

export default function RewardsDemo({
    snapshot, connected, onAction
}: {
    snapshot?: RewardsDemoSnapshot | null;
    connected: boolean;
    onAction: (action: RewardsDemoAction, code?: string) => void;
}) {
    const [code, setCode] = useState("");
    const [error, setError] = useState("");
    const codes = snapshot?.demoCodes ?? [];
    const disabled = !connected || !snapshot;

    return (
        <section className="rounded-3xl bg-brand-primary-strong p-5 text-white shadow-lg" aria-label="Simulated rewards">
            <div className="inline-flex items-center gap-2 rounded-full bg-white px-4 py-2 text-sm font-bold text-brand-primary-strong">
                <Flame aria-hidden="true" size={18} /> Rewards · demo only
            </div>
            <h2 className="mt-4 text-2xl font-black">Check in. Earn. Enjoy.</h2>
            <p className="mt-2 text-sm">Simulated drive-thru / kiosk check-in. No real account, payment or purchase. Never enter real member information.</p>
            <p className="mt-2 text-xs">All menu prices and 8% tax are illustrative samples, not restaurant 036309 quotes.</p>
            {!connected && <p role="status" className="mt-3 text-sm">Rewards are offline. Reconnect the ordering session to continue.</p>}
            {connected && !snapshot && (
                <Button className="mt-3 bg-white text-brand-primary-strong hover:bg-white/90" onClick={() => onAction("status")}>Load demo rewards</Button>
            )}
            {!snapshot?.checkedIn ? (
                <form className="mt-4 space-y-2" onSubmit={event => {
                    event.preventDefault();
                    const candidate = code.trim().toUpperCase();
                    if (!codes.includes(candidate)) {
                        setError("Use only a fictional demo code shown below. No value was sent.");
                        return;
                    }
                    setError("");
                    setCode("");
                    onAction("check_in", candidate);
                }}>
                    <label htmlFor="demo-rewards-code" className="block text-sm font-bold">Demo rewards / check-in code</label>
                    <input id="demo-rewards-code" autoComplete="off" spellCheck={false} maxLength={16}
                        value={code} onChange={event => setCode(event.target.value)} disabled={disabled}
                        aria-describedby="demo-code-help" aria-invalid={Boolean(error)}
                        className="w-full rounded-xl border-2 border-white bg-white px-3 py-2 text-slate-950 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white" />
                    <p id="demo-code-help" className="text-xs">
                        {codes.join(" or ")}. DEMO-PLUS starts with 500 fictional points; DEMO-NEW starts with 0.
                    </p>
                    {error && <p role="alert" className="text-sm font-bold">{error}</p>}
                    <Button type="submit" disabled={disabled} className="bg-white text-brand-primary-strong hover:bg-white/90">Check in to demo order</Button>
                </form>
            ) : (
                <div className="mt-4 space-y-3">
                    <p className="text-sm font-bold">Demo check-in linked to this order</p>
                    <p className="text-3xl font-black">{snapshot.balance} <span className="text-sm">demo points</span></p>
                    <p className="text-sm">{snapshot.pointsPerDollar} points per paid pre-tax demo dollar, rounded down per order. Earn {snapshot.pointsToEarn} points on completion.</p>
                    <div className="rounded-2xl border border-white/50 p-3">
                        <p className="flex items-center gap-2 font-bold"><Gift aria-hidden="true" size={18} /> {snapshot.rewardItem} · {snapshot.rewardCost} demo points</p>
                        <p className="mt-1 text-sm">
                            {snapshot.completed ? "Demo order completed. Start the next order to use more rewards." :
                                snapshot.redeemed ? "One item covered. Points reserved until completion." :
                                snapshot.canRedeem ? "You have an eligible reward! Redeem it on this order." :
                                    snapshot.balance >= snapshot.rewardCost ? `Add a plain ${snapshot.rewardItem} to this order to use the sample reward.` :
                                        "Keep earning demo points to unlock the sample reward."}
                        </p>
                        <Button disabled={disabled || snapshot.completed || (!snapshot.canRedeem && !snapshot.redeemed)}
                            onClick={() => onAction(snapshot.redeemed ? "unredeem" : "redeem")}
                            className="mt-3 bg-white text-brand-primary-strong hover:bg-white/90">
                            {snapshot.redeemed ? "Remove demo reward" : "Redeem demo reward"}
                        </Button>
                    </div>
                    <Button disabled={disabled || snapshot.completed} onClick={() => onAction("complete")}
                        className="border-2 border-white bg-transparent text-white hover:bg-white/10">Complete demo order</Button>
                    {snapshot.completed && (
                        <div>
                            <p className="text-sm">Completed · {snapshot.pointsAwarded} fictional points earned.</p>
                            <Button disabled={disabled} onClick={() => onAction("reset")} className="mt-2 bg-white text-brand-primary-strong hover:bg-white/90">Start next demo order</Button>
                        </div>
                    )}
                </div>
            )}
            <p role="status" aria-live="polite" className="mt-4 text-sm">{snapshot?.message}</p>
            <p className="mt-3 text-xs">Simplified simulation: one sample reward per order. No real tiers, expiry, signup or account sync. Session-only balances disappear when the session ends.</p>
        </section>
    );
}
